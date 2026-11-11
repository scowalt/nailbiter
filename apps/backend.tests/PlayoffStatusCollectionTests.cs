using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using backend.Data;
using backend.Models;
using backend.Services.PlayoffStatus;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Testcontainers.PostgreSql;
using Xunit;

namespace backend.tests;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class PlayoffStatusCollectionTests
{
    private const string RefreshFailure = "Failed to refresh {Sport} ratings from PlayoffStatus";
    private const string CollectionFailure = "PlayoffStatus {League} collection failed";
    private static readonly TimeSpan Poll = TimeSpan.FromHours(1);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private static readonly Dictionary<string, (string Standings, string[] Teams)[]> Pages = new()
    {
        ["nfl"] = [("/nfl/afcstandings.html", ["Buffalo Bills"]), ("/nfl/nfcstandings.html", ["Dallas Cowboys"])],
        ["nba"] = [("/nba/easternstandings.html", ["Boston Celtics"]), ("/nba/westernstandings.html", ["Los Angeles Lakers"])],
        ["mlb"] = [("/mlb/americanstandings.html", ["New York Yankees"]), ("/mlb/nationalstandings.html", ["Los Angeles Dodgers"])]
    };

    private static readonly (string League, string Team, double Playoff, double Championship)[] Expected =
    [
        ("mlb", "LAD", 0.75, 0.12), ("mlb", "NYY", 0.75, 0.12),
        ("nba", "BOS", 0.75, 0.12), ("nba", "LAL", 0.75, 0.12),
        ("nfl", "BUF", 0.75, 0.12), ("nfl", "DAL", 0.75, 0.12)
    ];

    [Fact]
    public async Task One_cycle_stores_rows_for_each_league()
    {
        await using var app = await CollectorApp.StartAsync(Healthy);
        await app.StartWorkerAsync();

        Assert.Equal(Poll, await app.NextWaitAsync());

        Assert.Equal(Expected, (await app.RatingsAsync()).Select(r => (r.Sport, r.TeamAbbreviation, r.PlayoffProbability!.Value, r.ChampionshipProbability!.Value)));
        Assert.All(await app.RatingsAsync(), r => Assert.Equal((2026, app.Clock.GetUtcNow().UtcDateTime), (r.Season, r.FetchedAt)));
        Assert.Equal(9, app.Provider.Requests.Count);
        Assert.Equal(new[] { "mlb", "nba", "nfl" }, (await app.StatesAsync()).Select(s => s.League));
        Assert.All(await app.StatesAsync(), s => Assert.Equal(app.Clock.GetUtcNow().Add(Interval).UtcTicks, s.NextAttemptAtTicks));
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Persisted_claim_stops_a_refetch_after_a_restart_until_the_daily_boundary()
    {
        await using var app = await CollectorApp.StartAsync(Healthy);
        await app.StartWorkerAsync();
        Assert.Equal(Poll, await app.NextWaitAsync());
        Assert.Equal(9, app.Provider.Requests.Count);

        await app.RestartAsync();
        Assert.Equal(Poll, await app.NextWaitAsync());
        Assert.Equal(9, app.Provider.Requests.Count);

        await app.AdvanceCyclesAsync(23);
        Assert.Equal(9, app.Provider.Requests.Count);

        await app.AdvanceCyclesAsync(1);
        Assert.Equal(18, app.Provider.Requests.Count);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Two_instances_starting_together_fetch_each_league_once()
    {
        await using var app = await CollectorApp.StartAsync(Healthy);
        await Task.WhenAll(app.StartWorkerAsync(), app.StartPeerWorkerAsync());

        Assert.Equal(Poll, await app.NextWaitAsync());
        Assert.Equal(Poll, await app.NextWaitAsync());

        Assert.Equal(9, app.Provider.Requests.Count);
        Assert.Equal(6, (await app.RatingsAsync()).Count);
    }

    [Fact]
    public async Task Mlb_offseason_is_claimed_without_requests()
    {
        await using var app = await CollectorApp.StartAsync(Healthy);
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 12, 15, 12, 0, 0, TimeSpan.Zero));
        await app.StartWorkerAsync();

        Assert.Equal(Poll, await app.NextWaitAsync());

        Assert.DoesNotContain(app.Provider.Requests, uri => uri.AbsolutePath.StartsWith("/mlb/", StringComparison.Ordinal));
        Assert.Equal(6, app.Provider.Requests.Count);
        Assert.Equal(new[] { ("nba", 2027), ("nfl", 2026) }, (await app.RatingsAsync()).Select(r => (r.Sport, r.Season)).Distinct());
        Assert.All(await app.StatesAsync(), s => Assert.Equal(app.Clock.GetUtcNow().Add(Interval).UtcTicks, s.NextAttemptAtTicks));
        Assert.Equal(3, (await app.StatesAsync()).Count);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Disabled_switch_makes_no_requests_and_claims_nothing()
    {
        await using var app = await CollectorApp.StartAsync(Healthy, enabled: false);
        await app.StartWorkerAsync();

        await app.Worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(app.Worker.ExecuteTask.IsCompletedSuccessfully);
        Assert.Empty(app.Provider.Requests);
        Assert.Empty(await app.StatesAsync());
        Assert.Empty(await app.RatingsAsync());
        Assert.Empty(await app.SentEventsAsync());
    }

    [Theory]
    [InlineData("storage")]
    [InlineData("claim")]
    public async Task Own_fault_is_sent_once_at_error_and_the_loop_continues(string site)
    {
        var fault = new FaultOnce(site);
        await using var app = await CollectorApp.StartAsync(Healthy, configureDb: db => db.AddInterceptors(fault));
        await app.StartWorkerAsync();

        Assert.Equal(Poll, await app.NextWaitAsync());

        var reported = Assert.Single(await app.SentEventsAsync());
        Assert.Equal("error", reported.Level);
        Assert.Contains(reported.Exceptions, e => e.Type == typeof(InvalidOperationException).FullName);
        Assert.Equal(site == "storage"
            ? (typeof(PlayoffStatusService).FullName, RefreshFailure)
            : (typeof(PlayoffStatusCollectionBackgroundService).FullName, CollectionFailure), (reported.Logger, reported.Template));
        Assert.Equal(new[] { "mlb", "nba" }, (await app.RatingsAsync()).Select(r => r.Sport).Distinct());

        await app.AdvanceCyclesAsync(24);
        Assert.Equal(6, (await app.RatingsAsync()).Count);
        Assert.Single(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Missing_page_is_a_warning_without_an_event()
    {
        await using var app = await CollectorApp.StartAsync(uri => uri.AbsolutePath.StartsWith("/nfl/", StringComparison.Ordinal)
            ? new Reply(HttpStatusCode.NotFound, "") : Healthy(uri));
        await app.StartWorkerAsync();

        Assert.Equal(Poll, await app.NextWaitAsync());

        var warning = Assert.Single(app.Logs, l => l.Category == typeof(PlayoffStatusService).FullName && l.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(HttpStatusCode.NotFound, Assert.IsType<HttpRequestException>(warning.Exception).StatusCode);
        Assert.Empty(await app.SentEventsAsync());
        Assert.Equal(new[] { "mlb", "nba" }, (await app.RatingsAsync()).Select(r => r.Sport).Distinct());
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body><p>Standings are being updated</p></body></html>")]
    [InlineData("<html><body><table><tr data-idx")]
    public async Task Malformed_page_is_a_warning_without_an_event(string body)
    {
        await using var app = await CollectorApp.StartAsync(uri => uri.AbsolutePath.StartsWith("/nfl/", StringComparison.Ordinal)
            ? new Reply(HttpStatusCode.OK, body) : Healthy(uri));
        await app.StartWorkerAsync();

        Assert.Equal(Poll, await app.NextWaitAsync());

        var rejected = app.Logs.Where(l => l.Category.StartsWith("backend.Services.PlayoffStatus.", StringComparison.Ordinal) && l.Level >= LogLevel.Warning).ToArray();
        Assert.NotEmpty(rejected);
        Assert.All(rejected, l => Assert.Equal(LogLevel.Warning, l.Level));
        Assert.Empty(await app.SentEventsAsync());
        Assert.Equal(new[] { "mlb", "nba" }, (await app.RatingsAsync()).Select(r => r.Sport).Distinct());
    }

    [Fact]
    public async Task Own_deadline_on_a_stalled_page_is_sent_once_at_error()
    {
        await using var app = await CollectorApp.StartAsync(uri => uri.AbsolutePath.StartsWith("/nfl/", StringComparison.Ordinal) ? new Hanging() : Healthy(uri),
            services => services.AddHttpClient(nameof(PlayoffStatusService)).ConfigureHttpClient(client => client.Timeout = TimeSpan.FromMilliseconds(100)));
        await app.StartWorkerAsync();

        Assert.Equal(Poll, await app.NextWaitAsync());

        var reported = Assert.Single(await app.SentEventsAsync());
        Assert.Equal((typeof(PlayoffStatusService).FullName, "error", RefreshFailure), (reported.Logger, reported.Level, reported.Template));
        Assert.Contains(reported.Exceptions, e => e.Type == typeof(TaskCanceledException).FullName);
        Assert.Equal(new[] { "mlb", "nba" }, (await app.RatingsAsync()).Select(r => r.Sport).Distinct());
    }

    [Fact]
    public async Task Shutdown_during_a_fetch_is_quiet()
    {
        await using var app = await CollectorApp.StartAsync(_ => new Hanging());
        await app.StartWorkerAsync();
        await app.Provider.HangStarted.WaitAsync(TimeSpan.FromSeconds(30));

        await app.StopWorkerAsync();

        Assert.True(app.Worker.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Single(app.Provider.Requests);
        Assert.DoesNotContain(app.Logs, l => l.Category.StartsWith("backend.Services.PlayoffStatus.", StringComparison.Ordinal) && l.Level >= LogLevel.Warning);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Migration_is_repeatable_and_backs_the_daily_claim()
    {
        await using var app = await CollectorApp.StartAsync(Healthy);
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "041_PlayoffStatusCollection.sql"));
        await app.ExecuteAsync("DROP TABLE playoffstatus_collection_state");
        await app.ExecuteAsync(sql);
        await app.ExecuteAsync(sql);

        await app.StartWorkerAsync();
        Assert.Equal(Poll, await app.NextWaitAsync());

        Assert.Equal(6, (await app.RatingsAsync()).Count);
        Assert.All(await app.StatesAsync(), s => Assert.Equal(app.Clock.GetUtcNow().Add(Interval).UtcTicks, s.NextAttemptAtTicks));
        var league = await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteAsync("INSERT INTO playoffstatus_collection_state VALUES ('nhl', 1)"));
        Assert.Equal(("23514", "ck_playoffstatus_collection_league"), (league.SqlState, league.ConstraintName));
        var ticks = await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteAsync("UPDATE playoffstatus_collection_state SET next_attempt_at_ticks = 0 WHERE league = 'nfl'"));
        Assert.Equal(("23514", "ck_playoffstatus_collection_ticks"), (ticks.SqlState, ticks.ConstraintName));
    }

    private static Answer Healthy(Uri uri)
    {
        foreach (var (league, pages) in Pages)
        {
            if (uri.AbsolutePath == $"/{league}/{league}postseasonprob.html")
                return new Reply(HttpStatusCode.OK, PostseasonPage(pages.SelectMany(p => p.Teams)));
            if (pages.FirstOrDefault(p => p.Standings == uri.AbsolutePath).Teams is { } teams)
                return new Reply(HttpStatusCode.OK, StandingsPage(teams));
        }
        throw new InvalidOperationException("Unexpected offline request: " + uri);
    }

    private static string StandingsPage(IEnumerable<string> teams) =>
        "<html><body><table><tr><th>Team</th><th>W</th><th>L</th><th>Pct</th><th>No Playoffs</th></tr>"
        + string.Concat(teams.Select((team, i) => $"<tr data-idx=\"{i}\"><td><a href=\"#\">{team}</a></td><td>9</td><td>4</td><td>69</td><td>25%</td></tr>"))
        + "</table></body></html>";

    private static string PostseasonPage(IEnumerable<string> teams) =>
        "<html><body><table><tr><th colspan=\"6\">Postseason</th></tr><tr><th>Team</th><th>R1</th><th>R2</th><th>R3</th><th>R4</th><th>Champion</th></tr>"
        + string.Concat(teams.Select(team => $"<tr><td><a href=\"#\">{team}</a></td><td>70%</td><td>45%</td><td>30%</td><td>20%</td><td>12%</td></tr>"))
        + "</table></body></html>";

    private abstract record Answer;

    private sealed record Reply(HttpStatusCode Status, string Body) : Answer;

    private sealed record Hanging : Answer;

    private sealed record SentEvent(string? Logger, string? Level, string? Template, (string? Type, string? Value)[] Exceptions);

    private sealed record LoggedEntry(string Category, LogLevel Level, Exception? Exception);

    private sealed class CollectorApp : IAsyncDisposable
    {
        private readonly PostgreSqlContainer database = IsolatedPostgreSql.CreateContainer();
        private readonly AppFactory entryPoint = new();
        private readonly SentTransport transport = new();
        private readonly LogRecorder logs = new();
        private readonly List<PlayoffStatusCollectionBackgroundService> workers = [];
        private readonly Action<IServiceCollection>? configure;
        private readonly Action<DbContextOptionsBuilder>? configureDb;
        private readonly bool enabled;
        private WebApplicationFactory<Program> app = null!;

        private CollectorApp(Func<Uri, Answer> answer, Action<IServiceCollection>? configure, Action<DbContextOptionsBuilder>? configureDb, bool enabled)
        {
            Provider = new SyntheticProvider(answer);
            this.configure = configure;
            this.configureDb = configureDb;
            this.enabled = enabled;
        }

        public LoopClock Clock { get; } = new(new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero));

        public SyntheticProvider Provider { get; }

        public IEnumerable<LoggedEntry> Logs => logs.Entries;

        public PlayoffStatusCollectionBackgroundService Worker => workers[^1];

        public static async Task<CollectorApp> StartAsync(Func<Uri, Answer> answer, Action<IServiceCollection>? configure = null,
            Action<DbContextOptionsBuilder>? configureDb = null, bool enabled = true)
        {
            var collector = new CollectorApp(answer, configure, configureDb, enabled);
            await collector.database.StartAsync();
            collector.app = collector.CreateFactory();
            using var scope = collector.app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.EnsureCreatedAsync();
            return collector;
        }

        public async Task StartWorkerAsync()
        {
            var worker = ActivatorUtilities.CreateInstance<PlayoffStatusCollectionBackgroundService>(app.Services);
            lock (workers) workers.Add(worker);
            await worker.StartAsync(CancellationToken.None);
        }

        public Task StartPeerWorkerAsync() => StartWorkerAsync();

        public async Task StopWorkerAsync() => await Worker.StopAsync(CancellationToken.None);

        public async Task RestartAsync()
        {
            await StopWorkersAsync();
            await app.DisposeAsync();
            app = CreateFactory();
            await StartWorkerAsync();
        }

        public async Task AdvanceCyclesAsync(int hours)
        {
            for (var hour = 0; hour < hours; hour++)
            {
                Clock.Advance(Poll);
                Assert.Equal(Poll, await NextWaitAsync());
            }
        }

        public async Task<TimeSpan> NextWaitAsync() =>
            await Clock.LoopWaits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        public async Task<List<TeamRating>> RatingsAsync()
        {
            using var scope = app.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().TeamRatings.AsNoTracking()
                .Where(r => r.Source == "playoffstatus").OrderBy(r => r.Sport).ThenBy(r => r.TeamAbbreviation).ToListAsync();
        }

        public async Task<List<PlayoffStatusCollectionState>> StatesAsync()
        {
            using var scope = app.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().PlayoffStatusCollectionOperations.AsNoTracking()
                .OrderBy(s => s.League).ToListAsync();
        }

        public async Task ExecuteAsync(string sql)
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.ExecuteSqlRawAsync(sql);
        }

        public async Task<IReadOnlyCollection<SentEvent>> SentEventsAsync()
        {
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
            return transport.Events.ToArray();
        }

        private WebApplicationFactory<Program> CreateFactory() => entryPoint.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlayoffRatings:PlayoffStatusCollectionEnabled"] = enabled.ToString()
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
                services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
                services.AddDbContext<ExcitementDbContext>(options =>
                {
                    options.UseNpgsql(database.GetConnectionString());
                    configureDb?.Invoke(options);
                });
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(handlers => handlers.PrimaryHandler = Provider));
                services.AddSingleton<ILoggerProvider>(logs);
                services.PostConfigure<SentryAspNetCoreOptions>(options =>
                {
                    options.Dsn = "https://public@sentry.invalid/1";
                    options.Transport = transport;
                });
                configure?.Invoke(services);
            });
        });

        private async Task StopWorkersAsync()
        {
            foreach (var worker in workers)
            {
                await worker.StopAsync(CancellationToken.None);
                worker.Dispose();
            }
            workers.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await StopWorkersAsync();
            if (app is not null) await app.DisposeAsync();
            await entryPoint.DisposeAsync();
            Provider.Dispose();
            await database.DisposeAsync();
        }
    }

    private sealed class LoopClock(DateTimeOffset now) : FakeTimeProvider(now)
    {
        public Channel<TimeSpan> LoopWaits { get; } = Channel.CreateUnbounded<TimeSpan>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (state is not CancellationTokenSource) LoopWaits.Writer.TryWrite(dueTime);
            return timer;
        }
    }

    private sealed class SyntheticProvider(Func<Uri, Answer> answer) : HttpMessageHandler
    {
        private readonly TaskCompletionSource hanging = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<Uri> Requests { get; } = new();

        public Task HangStarted => hanging.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!);
            switch (answer(request.RequestUri!))
            {
                case Reply reply:
                    return new HttpResponseMessage(reply.Status) { Content = new StringContent(reply.Body) };
                default:
                    hanging.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    throw new UnreachableException();
            }
        }
    }

    private sealed class FaultOnce(string site) : DbCommandInterceptor
    {
        private int faulted;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            site == "storage" && command.CommandText.Contains("'playoffstatus'", StringComparison.Ordinal) ? Fault(result) : ValueTask.FromResult(result);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            site == "claim" && command.CommandText.Contains("INSERT INTO playoffstatus_collection_state", StringComparison.Ordinal) ? Fault(result) : ValueTask.FromResult(result);

        private ValueTask<T> Fault<T>(T result) => Interlocked.Exchange(ref faulted, 1) == 0
            ? throw new InvalidOperationException("Synthetic own-code fault in PlayoffStatus collection")
            : ValueTask.FromResult(result);
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
