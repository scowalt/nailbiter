using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using backend.Data;
using backend.Services;
using backend.Services.Backfill;
using backend.Services.Espn;
using backend.Services.Kalshi;
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
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Polly.Timeout;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Testcontainers.PostgreSql;
using Xunit;

namespace backend.tests;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class OddsProviderReportingTests
{
    private const int StandardAttempts = 4;
    private const string LockTimeout = ";Options=-c lock_timeout=500";
    private static readonly Answer Hang = new Hanging();
    private static readonly Answer Unavailable = new Reply(HttpStatusCode.ServiceUnavailable, "{}");
    private static readonly Answer NoEvents = new Reply(HttpStatusCode.OK, """{"events":[]}""");
    private static readonly DateTime LiveStart = new(2026, 11, 7, 0, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime MlbOriginalStart = new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime HistoricalStart = new(2026, 9, 13, 17, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly Fetch Historical = new(typeof(KalshiOddsService), "Failed to fetch Kalshi historical data for {Away} @ {Home}",
        async (services, ct) => await services.GetRequiredService<KalshiOddsService>().GetHistoricalOddsAsync("nfl", "PHI", "WSH", HistoricalStart, 60, ct));

    private static readonly Dictionary<string, Fetch> Scoreboards = new()
    {
        ["EspnOdds"] = new(typeof(EspnOddsService), "Failed to fetch ESPN scoreboard for {Sport} on {Date}",
            async (services, ct) => await services.GetRequiredService<EspnOddsService>().FetchScoreboardAsync("nba", "20261107", ct)),
        ["NflBackfill"] = new(typeof(NflBackfillService), "Failed to fetch ESPN scoreboard for {Year} ST{Type} Week {Week}",
            async (services, ct) => await services.GetRequiredService<NflBackfillService>().FetchScoreboardByWeekAsync(2026, 2, 1, ct)),
    };

    private static readonly Dictionary<string, (Fetch Fetch, Func<Uri, Answer> Answer)> KalshiFetches = new()
    {
        ["Markets"] = (LiveOdds("nba", "BOS", "NYK", LiveStart),
            uri => uri.AbsolutePath == "/trade-api/v2/markets" ? Hang : NotFound),
        ["MarketByTicker"] = (LiveOdds("nba", "BOS", "NYK", LiveStart),
            uri => uri.AbsolutePath == "/trade-api/v2/markets" ? EmptyCatalog : Hang),
        ["WorldCupMarkets"] = (new(typeof(KalshiOddsService), null,
                async (services, ct) => await services.GetRequiredService<KalshiOddsService>().GetWorldCupThreeWayLiveOddsAsync("USA", "MEX", LiveStart, ct)),
            uri => uri.AbsolutePath == "/trade-api/v2/markets" ? Hang : NotFound),
        ["SameDayMlbSchedule"] = (LiveOdds("mlb", "CHW", "CLE", MlbOriginalStart),
            uri => uri.AbsolutePath switch
            {
                "/trade-api/v2/markets" => new Reply(HttpStatusCode.OK, MlbScheduleChangeCatalog()),
                "/trade-api/v2/milestones" => Hang,
                _ => NotFound
            }),
        ["MarketTickerSearch"] = (new(typeof(KalshiOddsService), null,
                async (services, ct) => await services.GetRequiredService<KalshiOddsService>().FindMarketTickerAsync("nba", "BOS", "NYK", LiveStart, ct)),
            uri => uri.AbsolutePath == "/trade-api/v2/markets" ? Hang : NotFound),
        ["Historical"] = (Historical, _ => Hang),
    };

    public static TheoryData<string> ScoreboardNames => [.. Scoreboards.Keys];

    public static TheoryData<string> KalshiFetchNames => [.. KalshiFetches.Keys];

    public static TheoryData<string, HttpStatusCode, string> UnreadableScoreboards()
    {
        var cases = new TheoryData<string, HttpStatusCode, string>();
        foreach (var name in Scoreboards.Keys)
        {
            cases.Add(name, HttpStatusCode.OK, """{"events":[""");
            cases.Add(name, HttpStatusCode.OK, "");
            cases.Add(name, HttpStatusCode.NotFound, "{}");
        }
        return cases;
    }

    [Theory]
    [MemberData(nameof(ScoreboardNames))]
    public async Task Caller_cancellation_mid_scoreboard_fetch_propagates_without_an_event(string name)
    {
        await AssertCallerCancellationPropagatesAsync(Scoreboards[name], _ => Hang);
    }

    [Theory]
    [MemberData(nameof(UnreadableScoreboards))]
    public async Task Unreadable_or_missing_scoreboard_is_a_warning_without_an_event(string name, HttpStatusCode status, string body)
    {
        var scoreboard = Scoreboards[name];
        await using var app = new OddsApp(_ => new Reply(status, body));

        Assert.Null(await scoreboard.Run(app.Services, CancellationToken.None));

        var failures = app.FailuresFrom(scoreboard.Service);
        Assert.NotEmpty(failures);
        Assert.All(failures, failure => Assert.Equal(LogLevel.Warning, failure.Level));
        Assert.Empty(await app.SentEventsAsync());
    }

    [Theory]
    [MemberData(nameof(ScoreboardNames))]
    public async Task Scoreboard_server_errors_after_retries_are_sent_once_at_error(string name)
    {
        var scoreboard = Scoreboards[name];
        await using var app = new OddsApp(_ => Unavailable);

        Assert.Null(await scoreboard.Run(app.Services, CancellationToken.None));

        Assert.Equal(StandardAttempts, app.Provider.Requests.Count);
        AssertSentOnceAtError(await app.SentEventsAsync(), scoreboard.Service, scoreboard.Template, typeof(HttpRequestException));
    }

    [Theory]
    [MemberData(nameof(ScoreboardNames))]
    public async Task Scoreboard_timeout_after_retries_is_sent_once_at_error(string name)
    {
        var scoreboard = Scoreboards[name];
        await using var app = new OddsApp(_ => Hang, services => services.ConfigureAll<HttpStandardResilienceOptions>(options =>
            options.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(50)));

        Assert.Null(await scoreboard.Run(app.Services, CancellationToken.None));

        Assert.Equal(StandardAttempts, app.Provider.Requests.Count);
        AssertSentOnceAtError(await app.SentEventsAsync(), scoreboard.Service, scoreboard.Template, typeof(TimeoutRejectedException));
    }

    [Fact]
    public async Task Live_scrape_leaves_each_scoreboard_failure_to_the_fetch_that_sent_it()
    {
        await using var app = new OddsApp(_ => Unavailable);

        Assert.Empty(await app.Services.GetRequiredService<EspnOddsService>().ScrapeAndStoreOddsAsync("world_cup", CancellationToken.None));

        var sent = await app.SentEventsAsync();
        Assert.Equal(3, sent.Count);
        Assert.All(sent, reported => Assert.Equal((typeof(EspnOddsService).FullName, "error", Scoreboards["EspnOdds"].Template),
            (reported.Logger, reported.Level, reported.Template)));
    }

    [Fact]
    public async Task Backfill_leaves_the_scoreboard_failure_to_the_fetch_that_sent_it()
    {
        await using var app = new OddsApp(_ => Unavailable);

        var result = await app.Services.GetRequiredService<NflBackfillService>().BackfillSeasonAsync(2026, OneWeek(), CancellationToken.None);

        Assert.Empty(result.Errors);
        AssertSentOnceAtError(await app.SentEventsAsync(), Scoreboards["NflBackfill"].Service, Scoreboards["NflBackfill"].Template, typeof(HttpRequestException));
    }

    [Theory]
    [MemberData(nameof(KalshiFetchNames))]
    public async Task Caller_cancellation_mid_kalshi_fetch_propagates_without_an_event(string name)
    {
        var (fetch, answer) = KalshiFetches[name];
        await AssertCallerCancellationPropagatesAsync(fetch, answer, PinClockForMlb);
    }

    [Theory]
    [InlineData("""{"candlesticks":[""")]
    [InlineData("")]
    public async Task Unreadable_kalshi_history_is_a_warning_without_an_event(string body)
    {
        await using var app = new OddsApp(_ => new Reply(HttpStatusCode.OK, body));

        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<KalshiHistoricalOdds>>(await Historical.Run(app.Services, CancellationToken.None)));

        Assert.Equal(LogLevel.Warning, Assert.Single(app.FailuresFrom(typeof(KalshiOddsService))).Level);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Kalshi_history_the_provider_does_not_have_sends_nothing()
    {
        await using var app = new OddsApp(_ => NotFound);

        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<KalshiHistoricalOdds>>(await Historical.Run(app.Services, CancellationToken.None)));

        Assert.DoesNotContain(app.Logs, l => l.Level >= LogLevel.Warning && l.Category.StartsWith("backend", StringComparison.Ordinal));
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Kalshi_history_server_errors_after_retries_are_sent_once_at_error()
    {
        await using var app = new OddsApp(_ => Unavailable);

        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<KalshiHistoricalOdds>>(await Historical.Run(app.Services, CancellationToken.None)));

        Assert.Equal(StandardAttempts, app.Provider.Requests.Count);
        AssertSentOnceAtError(await app.SentEventsAsync(), Historical.Service, Historical.Template, typeof(HttpRequestException));
    }

    [Fact]
    public async Task Kalshi_history_outage_is_sent_once_per_backfill_pass()
    {
        await using var app = new OddsApp(_ => Unavailable);

        var kalshi = app.Services.GetRequiredService<KalshiOddsService>();
        foreach (var _ in Enumerable.Range(0, 3))
            Assert.Empty(await kalshi.GetHistoricalOddsAsync("nfl", "PHI", "WSH", HistoricalStart, 60, CancellationToken.None));

        Assert.Equal(3 * StandardAttempts, app.Provider.Requests.Count);
        Assert.Equal([LogLevel.Error, LogLevel.Warning, LogLevel.Warning], app.FailuresFrom(typeof(KalshiOddsService)).Select(f => f.Level));
        AssertSentOnceAtError(await app.SentEventsAsync(), Historical.Service, Historical.Template, typeof(HttpRequestException));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Kalshi_history_refusal_is_sent_once_and_ends_the_backfill_pass(HttpStatusCode status)
    {
        await using var app = new OddsApp(_ => new Reply(status, "{}"));

        var kalshi = app.Services.GetRequiredService<KalshiOddsService>();
        foreach (var _ in Enumerable.Range(0, 3))
            Assert.Empty(await kalshi.GetHistoricalOddsAsync("nfl", "PHI", "WSH", HistoricalStart, 60, CancellationToken.None));

        Assert.Single(app.Provider.Requests);
        var reported = Assert.Single(await app.SentEventsAsync());
        Assert.Equal((typeof(KalshiOddsService).FullName, "error", Historical.Template, "backend.Services.KalshiAccessRefusedException"),
            (reported.Logger, reported.Level, reported.Template, reported.Exceptions[^1].Type));
    }

    [Fact]
    public async Task Caller_cancellation_mid_backfill_propagates_without_an_event()
    {
        var board = BackfillBoard();
        await using var database = await OddsDatabase.StartAsync();
        await using var app = new OddsApp(uri => uri.Host == "sports.core.api.espn.com" ? Hang : new Reply(HttpStatusCode.OK, board),
            database.Use());
        using var caller = new CancellationTokenSource();

        var backfill = app.Services.GetRequiredService<NflBackfillService>().BackfillSeasonAsync(2026, OneWeek(), caller.Token);
        await app.Provider.HangStarted.WaitAsync(TimeSpan.FromSeconds(30));
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backfill);
        Assert.DoesNotContain(app.Logs, l => l.Level >= LogLevel.Warning && l.Category.StartsWith("backend", StringComparison.Ordinal));
        Assert.Empty(await app.SentEventsAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}", LogLevel.Error)]
    [InlineData(HttpStatusCode.OK, """{"items":[""", LogLevel.Warning)]
    [InlineData(HttpStatusCode.NotFound, "{}", LogLevel.Warning)]
    public async Task Backfill_core_odds_failure_is_sent_only_when_the_provider_failed(HttpStatusCode status, string body, LogLevel level)
    {
        var board = BackfillBoard();
        await using var database = await OddsDatabase.StartAsync();
        await using var app = new OddsApp(uri => uri.Host == "sports.core.api.espn.com" ? new Reply(status, body) : new Reply(HttpStatusCode.OK, board),
            database.Use());

        var result = await app.Services.GetRequiredService<NflBackfillService>().BackfillSeasonAsync(2026, OneWeek(), CancellationToken.None);

        Assert.Equal((1, 0), (result.GamesCreated, result.Errors.Count));
        Assert.Equal(level, Assert.Single(app.FailuresFrom(typeof(NflBackfillService))).Level);
        var sent = await app.SentEventsAsync();
        if (level == LogLevel.Error) AssertSentOnceAtError(sent, typeof(NflBackfillService), "Failed to fetch ESPN Core API odds for event {EventId}", typeof(HttpRequestException));
        else Assert.Empty(sent);
    }

    [Fact]
    public async Task Backfill_core_odds_outage_is_sent_once_per_pass()
    {
        var board = JsonNode.Parse(BackfillBoard())!;
        var events = board["events"]!.AsArray();
        var second = events[0]!.DeepClone();
        second["id"] = "401872986";
        events.Add(second);
        await using var database = await OddsDatabase.StartAsync();
        await using var app = new OddsApp(uri => uri.Host == "sports.core.api.espn.com" ? Unavailable : new Reply(HttpStatusCode.OK, board.ToJsonString()),
            database.Use());

        var result = await app.Services.GetRequiredService<NflBackfillService>().BackfillSeasonAsync(2026, OneWeek(), CancellationToken.None);

        Assert.Equal((2, 0), (result.GamesCreated, result.Errors.Count));
        Assert.Equal(2 * StandardAttempts, app.Provider.Requests.Count(r => r.Host == "sports.core.api.espn.com"));
        Assert.Equal([LogLevel.Error, LogLevel.Warning], app.FailuresFrom(typeof(NflBackfillService)).Select(f => f.Level));
        AssertSentOnceAtError(await app.SentEventsAsync(), typeof(NflBackfillService), "Failed to fetch ESPN Core API odds for event {EventId}", typeof(HttpRequestException));
    }

    [Theory]
    [InlineData("ACCESS EXCLUSIVE", "Error processing game {GameId}")]
    [InlineData("EXCLUSIVE", "Error processing {SeasonType} week {Week}")]
    public async Task Backfill_database_failure_is_sent_once_at_error(string lockMode, string template)
    {
        var board = BackfillBoard();
        await using var database = await OddsDatabase.StartAsync();
        await using var tableLock = await database.HoldLockAsync($"LOCK TABLE games IN {lockMode} MODE");
        await using var app = new OddsApp(_ => new Reply(HttpStatusCode.OK, board), database.Use(LockTimeout));

        var result = await app.Services.GetRequiredService<NflBackfillService>().BackfillSeasonAsync(2026, OneWeek(), CancellationToken.None);

        Assert.Single(result.Errors);
        var reported = Assert.Single(await app.SentEventsAsync());
        Assert.Equal((typeof(NflBackfillService).FullName, "error", template), (reported.Logger, reported.Level, reported.Template));
        Assert.StartsWith(PostgresErrorCodes.LockNotAvailable + ":", Assert.Single(reported.Exceptions, e => e.Type == typeof(PostgresException).FullName).Value);
    }

    [Fact]
    public async Task Scrape_loop_shutdown_mid_fetch_ends_quietly()
    {
        var clock = new FakeTimeProvider(Now);
        await using var app = new OddsApp(_ => Hang, services => PinClock(services, clock));
        var loop = ActivatorUtilities.CreateInstance<OddsScrapeBackgroundService>(app.Services);

        await loop.StartAsync(CancellationToken.None);
        await AdvanceUntilAsync(clock, () => app.Provider.HangStarted.IsCompleted, TimeSpan.FromSeconds(30));
        await loop.StopAsync(CancellationToken.None);

        Assert.True(loop.ExecuteTask!.IsCanceled);
        Assert.DoesNotContain(app.Logs, l => l.Level >= LogLevel.Warning && l.Category.StartsWith("backend", StringComparison.Ordinal));
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Scrape_loop_failure_outside_a_fetch_is_a_warning_and_the_loop_runs_again()
    {
        var clock = new FakeTimeProvider(Now);
        await using var app = new OddsApp(_ => NoEvents, services =>
        {
            PinClock(services, clock);
            services.RemoveAll<IGameUpdateService>();
            services.AddSingleton<IGameUpdateService>(_ => throw new InvalidOperationException("synthetic update service failure"));
        });
        var loop = ActivatorUtilities.CreateInstance<OddsScrapeBackgroundService>(app.Services);
        var failures = () => app.FailuresFrom(typeof(OddsScrapeBackgroundService));

        await loop.StartAsync(CancellationToken.None);
        await AdvanceUntilAsync(clock, () => failures().Count(f => f.Message == "Error during live odds scraping cycle") >= 2, TimeSpan.FromSeconds(30));
        await loop.StopAsync(CancellationToken.None);

        Assert.All(failures(), failure => Assert.Equal((LogLevel.Warning, typeof(InvalidOperationException)), (failure.Level, failure.Exception!.GetType())));
        Assert.Contains(failures(), failure => failure.Message == "Error during pregame odds refresh cycle");
        Assert.True(loop.ExecuteTask!.IsCanceled);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Scrape_loop_database_failure_is_sent_once_per_sport_pass_and_the_loop_runs_again()
    {
        var clock = new FakeTimeProvider(Now);
        await using var database = await OddsDatabase.StartAsync();
        await using var tableLock = await database.HoldLockAsync("LOCK TABLE games IN ACCESS EXCLUSIVE MODE");
        var useDatabase = database.Use(LockTimeout);
        await using var app = new OddsApp(_ => NoEvents, services =>
        {
            useDatabase(services);
            PinClock(services, clock);
        });
        var loop = ActivatorUtilities.CreateInstance<OddsScrapeBackgroundService>(app.Services);
        var failures = () => app.FailuresFrom(typeof(OddsScrapeBackgroundService));

        await loop.StartAsync(CancellationToken.None);
        await AdvanceUntilAsync(clock, () => failures().Count >= 10, TimeSpan.FromSeconds(60));
        await loop.StopAsync(CancellationToken.None);

        var sent = await app.SentEventsAsync();
        Assert.Equal(failures().Count, sent.Count);
        Assert.All(sent, reported =>
        {
            Assert.Equal((typeof(OddsScrapeBackgroundService).FullName, "error", "Failed to scrape live odds for {Sport}"),
                (reported.Logger, reported.Level, reported.Template));
            Assert.StartsWith(PostgresErrorCodes.LockNotAvailable + ":", Assert.Single(reported.Exceptions, e => e.Type == typeof(PostgresException).FullName).Value);
        });
        Assert.True(loop.ExecuteTask!.IsCanceled);
    }

    private static Fetch LiveOdds(string sport, string home, string away, DateTime start) => new(typeof(KalshiOddsService), null,
        async (services, ct) => await services.GetRequiredService<KalshiOddsService>().GetLiveOddsAsync(sport, home, away, start, ct));

    private static BackfillOptions OneWeek() => new()
    {
        SeasonType = 2,
        StartWeek = 1,
        EndWeek = 1,
        KalshiCandlestickInterval = 60
    };

    private static string BackfillBoard() =>
        ProHypeTests.Fixture("nfl-pregame-401872985").GetProperty("board").GetProperty("body").GetRawText();

    private static string MlbScheduleChangeCatalog()
    {
        var data = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "KalshiMlb", "schedule-change.json")))!;
        foreach (var market in data["catalog"]!["markets"]!.AsArray())
        {
            market!.AsObject().Remove("occurrence_datetime");
            market.AsObject().Remove("expected_expiration_time");
        }
        return data["catalog"]!.ToJsonString();
    }

    private static void PinClockForMlb(IServiceCollection services) =>
        PinClock(services, new FakeTimeProvider(new DateTimeOffset(MlbOriginalStart.AddHours(1.25))));

    private static void PinClock(IServiceCollection services, TimeProvider clock)
    {
        services.RemoveAll<TimeProvider>();
        services.AddSingleton(clock);
    }

    private static async Task AdvanceUntilAsync(FakeTimeProvider clock, Func<bool> done, TimeSpan deadline)
    {
        var timer = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(timer.Elapsed < deadline, "the odds scrape loop did not reach the expected cycle");
            await Task.Delay(100, CancellationToken.None);
            if (!done()) clock.Advance(TimeSpan.FromSeconds(30));
        }
    }

    private static async Task AssertCallerCancellationPropagatesAsync(Fetch fetch, Func<Uri, Answer> answer, Action<IServiceCollection>? configure = null)
    {
        await using var app = new OddsApp(answer, configure);
        using var caller = new CancellationTokenSource();

        var fetching = fetch.Run(app.Services, caller.Token);
        await app.Provider.HangStarted.WaitAsync(TimeSpan.FromSeconds(10));
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetching);
        Assert.DoesNotContain(app.Logs, l => l.Category == fetch.Service.FullName && l.Level >= LogLevel.Warning);
        Assert.Empty(await app.SentEventsAsync());
    }

    private static void AssertSentOnceAtError(IReadOnlyCollection<SentEvent> sent, Type service, string? template, Type exception)
    {
        var reported = Assert.Single(sent);
        Assert.Equal(service.FullName, reported.Logger);
        Assert.Equal("error", reported.Level);
        Assert.Equal(template, reported.Template);
        Assert.Contains(reported.Exceptions, e => e.Type == exception.FullName);
    }

    private static Answer NotFound => new Reply(HttpStatusCode.NotFound, "{}");

    private static Answer EmptyCatalog => new Reply(HttpStatusCode.OK, """{"markets":[],"cursor":""}""");

    private sealed record Fetch(Type Service, string? Template, Func<IServiceProvider, CancellationToken, Task<object?>> Run);

    private abstract record Answer;

    private sealed record Reply(HttpStatusCode Status, string Body) : Answer;

    private sealed record Hanging : Answer;

    private sealed record SentEvent(string? Logger, string? Level, string? Template, (string? Type, string? Value)[] Exceptions);

    private sealed record LoggedEntry(string Category, LogLevel Level, Exception? Exception, string? Message);

    private sealed class OddsDatabase : IAsyncDisposable
    {
        private readonly PostgreSqlContainer container = IsolatedPostgreSql.CreateContainer();

        public static async Task<OddsDatabase> StartAsync()
        {
            var database = new OddsDatabase();
            await database.container.StartAsync();
            await using var db = new ExcitementDbContext(new DbContextOptionsBuilder<ExcitementDbContext>().UseNpgsql(database.container.GetConnectionString()).Options);
            await db.Database.EnsureCreatedAsync();
            return database;
        }

        public Action<IServiceCollection> Use(string options = "") => services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
            services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
            services.AddDbContext<ExcitementDbContext>(builder => builder.UseNpgsql(container.GetConnectionString() + options));
        };

        public async Task<IAsyncDisposable> HoldLockAsync(string sql)
        {
            var connection = new NpgsqlConnection(container.GetConnectionString());
            await connection.OpenAsync();
            var transaction = await connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync();
            return new HeldLock(connection, transaction);
        }

        public ValueTask DisposeAsync() => container.DisposeAsync();
    }

    private sealed class HeldLock(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class OddsApp : IAsyncDisposable
    {
        private readonly AppFactory entry = new();
        private readonly WebApplicationFactory<Program> app;
        private readonly SentTransport transport = new();
        private readonly LogRecorder logs = new();

        public OddsApp(Func<Uri, Answer> answer, Action<IServiceCollection>? configure = null)
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

        public IEnumerable<LoggedEntry> Logs => logs.Entries;

        public IReadOnlyCollection<LoggedEntry> FailuresFrom(Type service) =>
            logs.Entries.Where(l => l.Category == service.FullName && l.Exception is not null).ToArray();

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

        public Task HangStarted => hanging.Task;

        public ConcurrentQueue<Uri> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!);
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
                entries.Enqueue(new LoggedEntry(category, logLevel, exception,
                    state is IReadOnlyList<KeyValuePair<string, object?>> values ? values.LastOrDefault().Value as string : null));
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
