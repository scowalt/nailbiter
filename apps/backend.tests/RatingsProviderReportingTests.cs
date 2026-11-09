using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using backend.Data;
using backend.Services;
using backend.Services.Espn;
using backend.Services.PowerRankingsGuru;
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
using Npgsql;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Testcontainers.PostgreSql;
using Xunit;

namespace backend.tests;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class RatingsProviderReportingTests
{
    private const int StandardAttempts = 4;
    private const string RouteStepTemplate = "Ratings refresh step {Step} failed";
    private static readonly Answer Hang = new Hanging();
    private static readonly Answer Unavailable = new Reply(HttpStatusCode.ServiceUnavailable, "{}");

    private static readonly Dictionary<string, Fetch> Ratings = new()
    {
        ["PowerRankingsGuru"] = new(typeof(PowerRankingsGuruService), "Failed to refresh {Sport} ratings from PowerRankingsGuru",
            (services, ct) => services.GetRequiredService<PowerRankingsGuruService>().RefreshNflRatingsAsync(ct)),
        ["EspnPowerIndex"] = new(typeof(EspnPowerIndexService), "Failed to refresh {Sport} ratings from ESPN",
            (services, ct) => services.GetRequiredService<EspnPowerIndexService>().RefreshNflRatingsAsync(ct)),
    };

    private static readonly Fetch NhlGuru = new(typeof(NhlPowerRankingsGuruService), "NHL Power Rankings Guru collection interrupted; original observations preserved",
        (services, ct) => services.GetRequiredService<PowerRankingsGuruService>().RefreshNhlRatingsAsync(ct));

    [Theory]
    [InlineData("PowerRankingsGuru")]
    [InlineData("EspnPowerIndex")]
    public async Task Caller_cancellation_mid_ratings_fetch_propagates_without_an_event(string source)
    {
        await using var app = new RatingsApp(_ => Hang);
        await AssertCallerCancellationPropagatesAsync(app, Ratings[source]);
    }

    [Theory]
    [InlineData("PowerRankingsGuru")]
    [InlineData("EspnPowerIndex")]
    public async Task Refused_ratings_are_a_warning_without_an_event(string source)
    {
        var ratings = Ratings[source];
        await using var app = new RatingsApp(_ => new Reply(HttpStatusCode.NotFound, "{}"));

        Assert.Equal(0, await ratings.Run(app.Services, CancellationToken.None));

        var failure = Assert.Single(app.Logs, l => l.Category == ratings.Service.FullName && l.Exception is not null);
        Assert.Equal(LogLevel.Warning, failure.Level);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Theory]
    [InlineData("PowerRankingsGuru", "<html><body><table class=\"rankings-table\"><tbody><tr>")]
    [InlineData("PowerRankingsGuru", "")]
    [InlineData("EspnPowerIndex", """{"teams":[""")]
    [InlineData("EspnPowerIndex", "")]
    public async Task Unreadable_ratings_are_rejected_without_an_event(string source, string body)
    {
        var ratings = Ratings[source];
        await using var database = await StartDatabaseAsync();
        await using var app = new RatingsApp(_ => new Reply(HttpStatusCode.OK, body), services => UseDatabase(services, database.GetConnectionString()));

        Assert.Equal(0, await ratings.Run(app.Services, CancellationToken.None));

        Assert.Contains(app.Logs, l => l.Category == ratings.Service.FullName && l.Level == LogLevel.Warning);
        Assert.DoesNotContain(app.Logs, l => l.Category == ratings.Service.FullName && l.Level >= LogLevel.Error);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Unreadable_espn_power_index_page_is_a_warning_carrying_its_json_error()
    {
        await using var database = await StartDatabaseAsync();
        await using var app = new RatingsApp(_ => new Reply(HttpStatusCode.OK, """{"teams":["""), services => UseDatabase(services, database.GetConnectionString()));

        Assert.Equal(0, await Ratings["EspnPowerIndex"].Run(app.Services, CancellationToken.None));

        var rejected = Assert.Single(app.Logs, l => l.Category == typeof(EspnPowerIndexService).FullName && l.Exception is not null);
        Assert.Equal(LogLevel.Warning, rejected.Level);
        Assert.IsAssignableFrom<JsonException>(rejected.Exception);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Theory]
    [InlineData("PowerRankingsGuru", "<html></html>")]
    [InlineData("EspnPowerIndex", "{}")]
    public async Task Own_fault_below_the_ratings_fetch_is_sent_once_at_error(string source, string body)
    {
        var ratings = Ratings[source];
        await using var app = new RatingsApp(_ => new Reply(HttpStatusCode.OK, body), services => services.RemoveAll<ExcitementDbContext>());

        Assert.Equal(0, await ratings.Run(app.Services, CancellationToken.None));

        AssertSentOnceAtError(await app.SentEventsAsync(), ratings, typeof(InvalidOperationException));
    }

    [Theory]
    [InlineData("PowerRankingsGuru")]
    [InlineData("EspnPowerIndex")]
    public async Task Client_deadline_on_the_ratings_fetch_is_sent_once_at_error(string source)
    {
        var ratings = Ratings[source];
        await using var app = new RatingsApp(_ => Hang, services =>
            services.AddHttpClient(ratings.Service.Name).ConfigureHttpClient(client => client.Timeout = TimeSpan.FromMilliseconds(200)));

        Assert.Equal(0, await ratings.Run(app.Services, CancellationToken.None));

        AssertSentOnceAtError(await app.SentEventsAsync(), ratings, typeof(TaskCanceledException));
    }

    [Fact]
    public async Task Own_fault_while_parsing_powerrankingsguru_html_is_sent_once_at_error()
    {
        await using var database = await StartDatabaseAsync();
        await using var app = new RatingsApp(_ => new Reply(HttpStatusCode.OK, "<html></html>"), services =>
        {
            UseDatabase(services, database.GetConnectionString());
            services.AddSingleton<ILoggerProvider>(new FaultyLogger(typeof(PowerRankingsGuruService).FullName!, "Rejected PowerRankingsGuru"));
        });

        Assert.Equal(0, await Ratings["PowerRankingsGuru"].Run(app.Services, CancellationToken.None));

        var reported = Assert.Single(await app.SentEventsAsync());
        Assert.Equal(typeof(PowerRankingsGuruService).FullName, reported.Logger);
        Assert.Equal("error", reported.Level);
        Assert.Equal("Failed to parse PowerRankingsGuru HTML for {Sport}", reported.Template);
        Assert.Contains(reported.Exceptions, e => e.Type == typeof(FaultyLoggerException).FullName);
    }

    [Theory]
    [InlineData("PowerRankingsGuru")]
    [InlineData("EspnPowerIndex")]
    public async Task Server_errors_after_retries_are_sent_once_at_error(string source)
    {
        var ratings = Ratings[source];
        await using var app = new RatingsApp(_ => Unavailable);

        Assert.Equal(0, await ratings.Run(app.Services, CancellationToken.None));

        Assert.Equal(StandardAttempts, app.Provider.Requests);
        AssertSentOnceAtError(await app.SentEventsAsync(), ratings, typeof(HttpRequestException));
    }

    [Theory]
    [InlineData("PowerRankingsGuru", "<html></html>")]
    [InlineData("EspnPowerIndex", "{}")]
    public async Task Database_failure_is_sent_once_at_error_with_its_sqlstate(string source, string body)
    {
        var ratings = Ratings[source];
        await using var database = await StartDatabaseAsync();
        await using var holder = new NpgsqlConnection(database.GetConnectionString());
        await holder.OpenAsync();
        await using var tableLock = await holder.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("LOCK TABLE team_ratings IN ACCESS EXCLUSIVE MODE", holder, tableLock))
            await lockCommand.ExecuteNonQueryAsync();
        await using var app = new RatingsApp(_ => new Reply(HttpStatusCode.OK, body),
            services => UseDatabase(services, database.GetConnectionString() + ";Options=-c lock_timeout=500"));

        Assert.Equal(0, await ratings.Run(app.Services, CancellationToken.None));

        var sent = await app.SentEventsAsync();
        AssertSentOnceAtError(sent, ratings, typeof(PostgresException));
        var failure = Assert.Single(sent.Single().Exceptions, e => e.Type == typeof(PostgresException).FullName);
        Assert.StartsWith(PostgresErrorCodes.LockNotAvailable + ":", failure.Value);
    }

    [Fact]
    public async Task Caller_cancellation_mid_nhl_guru_fetch_propagates_without_an_event()
    {
        await using var database = await StartDatabaseAsync();
        await using var app = new RatingsApp(_ => Hang, services => UseDatabase(services, database.GetConnectionString()));
        await AssertCallerCancellationPropagatesAsync(app, NhlGuru);
    }

    [Fact]
    public async Task Nhl_guru_client_deadline_is_sent_once_at_error_and_not_caller_cancellation()
    {
        await using var database = await StartDatabaseAsync();
        await using var app = new RatingsApp(_ => Hang, services =>
        {
            UseDatabase(services, database.GetConnectionString());
            services.AddHttpClient(nameof(NhlPowerRankingsGuruService)).ConfigureHttpClient(client => client.Timeout = TimeSpan.FromMilliseconds(200));
        });

        Assert.Equal(0, await NhlGuru.Run(app.Services, CancellationToken.None));

        var failure = Assert.Single(app.Logs, l => l.Category == NhlGuru.Service.FullName && l.Exception is not null);
        Assert.Equal(LogLevel.Error, failure.Level);
        Assert.IsAssignableFrom<OperationCanceledException>(failure.Exception);
        AssertSentOnceAtError(await app.SentEventsAsync(), NhlGuru, typeof(TaskCanceledException));
    }

    [Fact]
    public async Task Caller_cancellation_stops_the_ratings_refresh_cycle_without_an_event()
    {
        await using var app = new RatingsApp(_ => Hang);
        using var caller = new CancellationTokenSource();

        var refreshing = app.Background.RefreshAllRatingsAsync(caller.Token);
        await app.Provider.HangStarted.WaitAsync(TimeSpan.FromSeconds(10));
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refreshing);
        Assert.Equal(1, app.Provider.Requests);
        Assert.DoesNotContain(app.Logs, l => l.Category.StartsWith("backend", StringComparison.Ordinal) && l.Level >= LogLevel.Warning);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Caller_cancellation_at_the_last_standings_step_ends_the_cycle_without_an_event()
    {
        await using var database = await StartDatabaseAsync();
        await using var app = new RatingsApp(
            uri => uri.AbsolutePath.EndsWith("/baseball/mlb/standings", StringComparison.Ordinal) ? Hang : new Reply(HttpStatusCode.NotFound, "{}"),
            services => UseDatabase(services, database.GetConnectionString()));
        using var caller = new CancellationTokenSource();

        var refreshing = app.Background.RefreshAllRatingsAsync(caller.Token);
        await app.Provider.HangStarted.WaitAsync(TimeSpan.FromSeconds(30));
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refreshing);
        Assert.DoesNotContain(app.Logs, l => l.Category == typeof(RatingsRefreshBackgroundService).FullName && l.Level >= LogLevel.Warning);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Ratings_refresh_cycle_runs_every_step_and_leaves_each_failure_to_the_fetch_that_sent_it()
    {
        await using var database = await StartDatabaseAsync();
        await using var app = new RatingsApp(_ => Unavailable, services => UseDatabase(services, database.GetConnectionString()));

        await app.Background.RefreshAllRatingsAsync(CancellationToken.None);

        var sent = await app.SentEventsAsync();
        Assert.All(sent, e => Assert.Equal("error", e.Level));
        Assert.Equal(
            new[] { typeof(EspnPowerIndexService).FullName, typeof(EspnStandingsService).FullName, NhlGuru.Service.FullName, typeof(PowerRankingsGuruService).FullName }
                .OrderBy(logger => logger, StringComparer.Ordinal),
            sent.Select(e => e.Logger).Distinct().OrderBy(logger => logger, StringComparer.Ordinal));
        Assert.Single(sent, e => e.Logger == NhlGuru.Service.FullName);
    }

    [Fact]
    public async Task Own_fault_in_a_ratings_cycle_step_is_sent_once_and_the_cycle_carries_on()
    {
        await using var database = await StartDatabaseAsync();
        await using var app = new RatingsApp(_ => new Reply(HttpStatusCode.NotFound, "{}"), services =>
        {
            UseDatabase(services, database.GetConnectionString());
            services.RemoveAll<NhlPowerRankingsGuruService>();
        });

        await app.Background.RefreshAllRatingsAsync(CancellationToken.None);

        var reported = Assert.Single(await app.SentEventsAsync());
        Assert.Equal(typeof(RatingsRefreshBackgroundService).FullName, reported.Logger);
        Assert.Equal("error", reported.Level);
        Assert.Equal("Failed to refresh PowerRankingsGuru NHL rankings", reported.Template);
        Assert.Contains(reported.Exceptions, e => e.Type == typeof(InvalidOperationException).FullName);
        Assert.Contains(app.Provider.Seen, uri => uri.AbsolutePath.EndsWith("/baseball/mlb/standings", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Own_fault_in_a_ratings_route_step_is_sent_once_and_the_route_carries_on()
    {
        await using var database = await StartDatabaseAsync();
        await using var app = new RatingsApp(_ => new Reply(HttpStatusCode.NotFound, "{}"), services =>
        {
            UseDatabase(services, database.GetConnectionString());
            services.RemoveAll<NhlPowerRankingsGuruService>();
        });

        using var response = await app.CreateClient().PostAsync("/api/ratings/refresh", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var results = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.StartsWith("error: ", results.RootElement.GetProperty("nhl_guruPowerRankings").GetString());
        Assert.Equal(0, results.RootElement.GetProperty("mlb_powerRankings").GetInt32());
        var sent = await app.SentEventsAsync();
        var reported = Assert.Single(sent, e => e.Template == RouteStepTemplate);
        Assert.Equal("error", reported.Level);
        Assert.Contains(reported.Exceptions, e => e.Type == typeof(InvalidOperationException).FullName);
        Assert.DoesNotContain(sent, e => e.Logger == typeof(PowerRankingsGuruService).FullName || e.Logger == typeof(NhlPowerRankingsGuruService).FullName);
    }

    [Fact]
    public async Task Host_stop_while_waiting_for_the_next_ratings_cycle_is_quiet()
    {
        await using var database = await StartDatabaseAsync();
        await using var app = new RatingsApp(_ => new Reply(HttpStatusCode.NotFound, "{}"), services => UseDatabase(services, database.GetConnectionString()));
        var background = app.Background;

        await background.StartAsync(CancellationToken.None);
        var deadline = Stopwatch.StartNew();
        while (!app.Logs.Any(l => l.Message.StartsWith("Next ratings refresh scheduled", StringComparison.Ordinal)))
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromMinutes(2), "the first ratings cycle never reached its next-cycle wait");
            await Task.Delay(50);
        }
        Assert.Empty(await app.SentEventsAsync());
        var before = app.Logs.Count();

        await background.StopAsync(CancellationToken.None);

        Assert.DoesNotContain(app.Logs.Skip(before), l => l.Level >= LogLevel.Warning);
        Assert.Empty(await app.SentEventsAsync());
    }

    private static async Task AssertCallerCancellationPropagatesAsync(RatingsApp app, Fetch fetch)
    {
        using var caller = new CancellationTokenSource();

        var fetching = fetch.Run(app.Services, caller.Token);
        await app.Provider.HangStarted.WaitAsync(TimeSpan.FromSeconds(10));
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetching);
        Assert.DoesNotContain(app.Logs, l => l.Category == fetch.Service.FullName && l.Level >= LogLevel.Warning);
        Assert.Empty(await app.SentEventsAsync());
    }

    private static void AssertSentOnceAtError(IReadOnlyCollection<SentEvent> sent, Fetch fetch, Type exception)
    {
        var reported = Assert.Single(sent);
        Assert.Equal(fetch.Service.FullName, reported.Logger);
        Assert.Equal("error", reported.Level);
        Assert.Equal(fetch.Template, reported.Template);
        Assert.Contains(reported.Exceptions, e => e.Type == exception.FullName);
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = IsolatedPostgreSql.CreateContainer();
        await database.StartAsync();
        await using var db = new ExcitementDbContext(new DbContextOptionsBuilder<ExcitementDbContext>().UseNpgsql(database.GetConnectionString()).Options);
        await db.Database.EnsureCreatedAsync();
        return database;
    }

    private static void UseDatabase(IServiceCollection services, string connectionString)
    {
        services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
        services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
        services.AddDbContext<ExcitementDbContext>(db => db.UseNpgsql(connectionString));
    }

    private sealed record Fetch(Type Service, string? Template, Func<IServiceProvider, CancellationToken, Task<int>> Run);

    private abstract record Answer;

    private sealed record Reply(HttpStatusCode Status, string Body) : Answer;

    private sealed record Hanging : Answer;

    private sealed record SentEvent(string? Logger, string? Level, string? Template, (string? Type, string? Value)[] Exceptions);

    private sealed record LoggedEntry(string Category, LogLevel Level, Exception? Exception, string Message);

    private sealed class RatingsApp : IAsyncDisposable
    {
        private readonly AppFactory entry = new();
        private readonly WebApplicationFactory<Program> app;
        private readonly SentTransport transport = new();
        private readonly LogRecorder logs = new();

        public RatingsApp(Func<Uri, Answer> answer, Action<IServiceCollection>? configure = null)
        {
            Provider = new SyntheticProvider(answer);
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
                    services.ConfigureAll<HttpStandardResilienceOptions>(options => options.Retry.Delay = TimeSpan.Zero);
                    services.AddSingleton<ILoggerProvider>(logs);
                    services.PostConfigure<SentryAspNetCoreOptions>(options =>
                    {
                        options.Dsn = "https://public@sentry.invalid/1";
                        options.Transport = transport;
                    });
                    configure?.Invoke(services);
                });
            });
        }

        public SyntheticProvider Provider { get; }

        public IServiceProvider Services => app.Services;

        public HttpClient CreateClient() => app.CreateClient();

        public RatingsRefreshBackgroundService Background =>
            new(app.Services, app.Services.GetRequiredService<ILogger<RatingsRefreshBackgroundService>>());

        public IEnumerable<LoggedEntry> Logs => logs.Entries;

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
        }
    }

    private sealed class SyntheticProvider(Func<Uri, Answer> answer) : HttpMessageHandler
    {
        private readonly TaskCompletionSource hanging = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int requests;

        public Task HangStarted => hanging.Task;

        public int Requests => requests;

        public ConcurrentQueue<Uri> Seen { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            Seen.Enqueue(request.RequestUri!);
            if (answer(request.RequestUri!) is Reply reply)
                return new HttpResponseMessage(reply.Status) { Content = new StringContent(reply.Body) };
            hanging.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        }
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
                entries.Enqueue(new LoggedEntry(category, logLevel, exception, formatter(state, exception)));
        }
    }

    private sealed class FaultyLoggerException(string message) : Exception(message);

    private sealed class FaultyLogger(string category, string messagePrefix) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName == category, messagePrefix);

        public void Dispose() { }

        private sealed class Logger(bool faulty, string messagePrefix) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (faulty && formatter(state, exception).StartsWith(messagePrefix, StringComparison.Ordinal))
                    throw new FaultyLoggerException(messagePrefix);
            }
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
                var exceptions = root.TryGetProperty("exception", out var exception)
                    ? exception.GetProperty("values").EnumerateArray().Select(e => (Text(e, "type"), Text(e, "value"))).ToArray()
                    : [];
                var template = root.TryGetProperty("logentry", out var logEntry) ? Text(logEntry, "message") : null;
                Events.Enqueue(new SentEvent(Text(root, "logger"), Text(root, "level"), template, exceptions));
            }
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) ? value.GetString() : null;
    }
}
