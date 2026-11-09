using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Testcontainers.PostgreSql;
using Xunit;

namespace backend.tests;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class CollegeEventFailureReportingTests
{
    private const int StandardAttempts = 4;
    private const string Template = "College event {Event} missing; preserving stored state";
    private const string Failing = "401900002";
    private const string Healthy = "401900003";
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Event_that_times_out_is_sent_once_and_the_other_known_games_still_update()
    {
        await using var app = await CollegeApp.StartAsync(id => id == Failing ? Hang : FinalHeader(id),
            options => options.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(50));

        await app.RunAsync(CancellationToken.None);

        Assert.Equal(StandardAttempts, app.Provider.SummaryRequests(Failing));
        await AssertHealthyGameFinalAsync(app);
        var reported = Assert.Single(await app.SentEventsAsync());
        Assert.Equal((typeof(CollegeFootballService).FullName, "error", Template, typeof(TimeoutRejectedException).FullName),
            (reported.Logger, reported.Level, reported.Template, reported.ExceptionTypes[^1]));
    }

    [Fact]
    public async Task Events_rejected_by_an_open_circuit_are_each_sent_once_by_the_caller()
    {
        await using var app = await CollegeApp.StartAsync(_ => Unreachable,
            options => options.CircuitBreaker.MinimumThroughput = StandardAttempts);

        await app.RunAsync(CancellationToken.None);

        const int failuresThatOpenTheCircuit = 3;
        Assert.Equal(failuresThatOpenTheCircuit, app.Provider.SummaryRequests(Failing) + app.Provider.SummaryRequests(Healthy));
        var sent = await app.SentEventsAsync();
        Assert.Equal(2, sent.Count);
        Assert.All(sent, reported => Assert.Equal(
            (typeof(CollegeFootballService).FullName, "error", Template, typeof(HttpRequestException).FullName, typeof(BrokenCircuitException).FullName),
            (reported.Logger, reported.Level, reported.Template, reported.ExceptionTypes[0], reported.ExceptionTypes[^1])));
    }

    [Fact]
    public async Task Unreadable_event_is_a_warning_without_an_event_and_the_other_known_games_still_update()
    {
        await using var app = await CollegeApp.StartAsync(id => id == Failing ? Ok("<html>maintenance</html>") : FinalHeader(id));

        await app.RunAsync(CancellationToken.None);

        await AssertHealthyGameFinalAsync(app);
        var unreadable = Assert.Single(app.Logs, l => l.Category == typeof(CollegeFootballService).FullName && l.Exception is not null);
        Assert.Equal(LogLevel.Warning, unreadable.Level);
        Assert.IsAssignableFrom<JsonException>(unreadable.Exception);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Caller_cancellation_during_an_event_fetch_propagates_without_an_event()
    {
        await using var app = await CollegeApp.StartAsync(id => id == Failing ? Hang : FinalHeader(id));
        using var caller = new CancellationTokenSource();

        var run = app.RunAsync(caller.Token);
        await app.Provider.SummaryRequested(Failing).WaitAsync(TimeSpan.FromSeconds(30));
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Empty(await app.SentEventsAsync());
    }


    private static async Task AssertHealthyGameFinalAsync(CollegeApp app)
    {
        var game = await app.GameAsync(Healthy);
        Assert.Equal(("final", 34, 27), (game.Status, game.HomeScore, game.AwayScore));
    }

    private static Game KnownGame(string id) => new()
    {
        Id = id, Sport = "cfb", Status = "scheduled", SeasonYear = 2026, SeasonType = 2,
        HomeTeam = "Oregon", AwayTeam = "Rice", HomeTeamAbbreviation = "ORE", AwayTeamAbbreviation = "RICE",
        HomeTeamId = "2483", AwayTeamId = "242",
        HomeMarketTicker = $"KXNCAAFGAME-26SEP13RICEORE{id}-ORE", AwayMarketTicker = $"KXNCAAFGAME-26SEP13RICEORE{id}-RICE",
        ScheduledStart = Now.UtcDateTime.AddDays(-3)
    };

    private static Func<CancellationToken, Task<HttpResponseMessage>> FinalHeader(string id) => Ok($$$"""
        {"header":{"id":"{{{id}}}","date":"2026-09-13T19:00Z","season":{"year":2026,"type":2},"week":{"number":3},
         "competitions":[{"id":"{{{id}}}","date":"2026-09-13T19:00Z","status":{"period":4,"displayClock":"0:00","type":{"state":"post","name":"STATUS_FINAL","shortDetail":"Final"}},
          "competitors":[{"homeAway":"home","score":"34","team":{"id":"2483","abbreviation":"ORE","displayName":"Oregon"}},
                         {"homeAway":"away","score":"27","team":{"id":"242","abbreviation":"RICE","displayName":"Rice"}}]}]}}
        """);

    private static Func<CancellationToken, Task<HttpResponseMessage>> Unreachable =>
        _ => Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic provider connection failure"));

    private static async Task<HttpResponseMessage> Hang(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new UnreachableException();
    }

    private static Func<CancellationToken, Task<HttpResponseMessage>> Ok(string body) =>
        _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

    private const string Scoreboard = """
        {"leagues":[{"season":{"year":2026},"calendar":[{"value":"2","startDate":"2026-08-22T07:00Z"}]}],"events":[]}
        """;

    private const string ApPoll = """
        {"rankings":[{"id":"1","type":"ap","season":{"year":2026},"lastUpdated":"2026-09-13T21:19Z","ranks":[{"current":5,"team":{"id":"2483"}}]}]}
        """;

    private const string Roster = """
        {"requestedSeason":{"year":2026},"currentSeason":{"year":2026},"currentValues":{"season":2026,"conference":"80"},
         "pagination":{"page":1,"pages":1,"count":2},"lastUpdated":"2026-09-16T08:00Z",
         "categories":[{"name":"fpi","names":["fpirank","fpi"]}],
         "teams":[{"team":{"id":"2483","abbreviation":"ORE","displayName":"Oregon"},"categories":[{"name":"fpi","values":[2.0,25.0]}]},
                  {"team":{"id":"242","abbreviation":"RICE","displayName":"Rice"},"categories":[{"name":"fpi","values":[90.0,-12.0]}]}]}
        """;

    private sealed record SentEvent(string? Logger, string? Level, string? Template, string?[] ExceptionTypes);

    private sealed record LoggedEntry(string Category, LogLevel Level, Exception? Exception);

    private sealed class CollegeApp : IAsyncDisposable
    {
        private readonly AppFactory entry = new();
        private readonly PostgreSqlContainer database;
        private readonly WebApplicationFactory<Program> app;
        private readonly SentTransport transport = new();
        private readonly LogRecorder logs = new();

        private CollegeApp(PostgreSqlContainer database, Func<string, Func<CancellationToken, Task<HttpResponseMessage>>> summary,
            Action<HttpStandardResilienceOptions>? resilience)
        {
            this.database = database;
            Provider = new SyntheticProvider(uri => uri switch
            {
                { AbsolutePath: var path } when path.EndsWith("/summary", StringComparison.Ordinal) => summary(SyntheticProvider.EventId(uri)),
                { AbsolutePath: var path } when path.EndsWith("/powerindex", StringComparison.Ordinal) => Ok(Roster),
                { AbsolutePath: var path } when path.EndsWith("/rankings", StringComparison.Ordinal) => Ok(ApPoll),
                { AbsolutePath: var path } when path.EndsWith("/scoreboard", StringComparison.Ordinal) => Ok(Scoreboard),
                { Host: "sports.core.api.espn.com", AbsolutePath: var path } when path.EndsWith("/events", StringComparison.Ordinal) =>
                    Ok("""{"count":0,"pageCount":0,"pageIndex":0,"pageSize":25,"items":[]}"""),
                { AbsolutePath: "/trade-api/v2/markets" } => Ok("""{"markets":[],"cursor":""}"""),
                _ => _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") })
            });
            app = entry.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.ConfigureServices(services =>
                {
                    foreach (var worker in services.Where(s => s.ServiceType == typeof(IHostedService)
                        && s.ImplementationType?.Namespace?.StartsWith("backend", StringComparison.Ordinal) == true).ToArray())
                        services.Remove(worker);
                    services.ConfigureAll<HttpClientFactoryOptions>(options =>
                        options.HttpMessageHandlerBuilderActions.Add(handlers => handlers.PrimaryHandler = Provider));
                    services.ConfigureAll<HttpStandardResilienceOptions>(options =>
                    {
                        options.Retry.Delay = TimeSpan.Zero;
                        resilience?.Invoke(options);
                    });
                    services.AddSingleton<ILoggerProvider>(logs);
                    services.PostConfigure<SentryAspNetCoreOptions>(options =>
                    {
                        options.Dsn = "https://public@sentry.invalid/1";
                        options.Transport = transport;
                    });
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(new FixedClock());
                    services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
                    services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
                    services.AddDbContext<ExcitementDbContext>(options => options.UseNpgsql(database.GetConnectionString()));
                });
            });
        }

        public static async Task<CollegeApp> StartAsync(Func<string, Func<CancellationToken, Task<HttpResponseMessage>>> summary,
            Action<HttpStandardResilienceOptions>? resilience = null)
        {
            var database = IsolatedPostgreSql.CreateContainer();
            await database.StartAsync();
            var app = new CollegeApp(database, summary, resilience);
            using var scope = app.app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Games.AddRange(KnownGame(Failing), KnownGame(Healthy));
            await db.SaveChangesAsync();
            return app;
        }

        public SyntheticProvider Provider { get; }

        public IEnumerable<LoggedEntry> Logs => logs.Entries;

        public async Task RunAsync(CancellationToken ct)
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<CollegeFootballService>().RunAsync(false, true, ct);
        }

        public async Task<Game> GameAsync(string id)
        {
            using var scope = app.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Games.AsNoTracking().SingleAsync(g => g.Id == id);
        }

        public async Task<IReadOnlyCollection<SentEvent>> SentEventsAsync()
        {
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
            return transport.Events.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            await entry.DisposeAsync();
            Provider.Dispose();
            await database.DisposeAsync();
        }
    }

    private sealed class SyntheticProvider(Func<Uri, Func<CancellationToken, Task<HttpResponseMessage>>> respond) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> summaries = new();
        private readonly ConcurrentQueue<string> summaryRequests = new();

        public static string EventId(Uri uri) => System.Web.HttpUtility.ParseQueryString(uri.Query)["event"] ?? "";

        public Task SummaryRequested(string id) => Signal(id).Task;

        public int SummaryRequests(string id) => summaryRequests.Count(r => r == id);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/summary", StringComparison.Ordinal))
            {
                summaryRequests.Enqueue(EventId(uri));
                Signal(EventId(uri)).TrySetResult();
            }
            return respond(uri)(cancellationToken);
        }

        private TaskCompletionSource Signal(string id) =>
            summaries.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class LogRecorder : ILoggerProvider
    {
        public ConcurrentQueue<LoggedEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

        public void Dispose() { }

        private sealed class Logger(string category, ConcurrentQueue<LoggedEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LoggedEntry(category, logLevel, exception));
        }
    }

    private sealed class SentTransport : ITransport
    {
        public ConcurrentQueue<SentEvent> Events { get; } = new();

        public async Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            using var stream = new MemoryStream();
            await envelope.SerializeAsync(stream, null, cancellationToken);
            var lines = System.Text.Encoding.UTF8.GetString(stream.ToArray()).Split('\n');
            for (var i = 1; i + 1 < lines.Length; i++)
            {
                using var header = JsonDocument.Parse(lines[i]);
                if (header.RootElement.GetProperty("type").GetString() != "event") continue;
                using var payload = JsonDocument.Parse(lines[++i]);
                var root = payload.RootElement;
                var exceptionTypes = root.TryGetProperty("exception", out var exception)
                    ? exception.GetProperty("values").EnumerateArray().Select(e => Text(e, "type")).ToArray()
                    : [];
                var template = root.TryGetProperty("logentry", out var logEntry) ? Text(logEntry, "message") : null;
                Events.Enqueue(new SentEvent(Text(root, "logger"), Text(root, "level"), template, exceptionTypes));
            }
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) ? value.GetString() : null;
    }
}
