using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using backend.Data;
using backend.Models;
using backend.Services;
using backend.Services.Backfill;
using backend.Services.Espn;
using backend.Services.Kalshi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    private const string CoreOddsTemplate = "Failed to fetch ESPN Core API odds for event {EventId}";
    private const string PregameKalshiTemplate = "Failed to fetch Kalshi pregame odds for {Game}";
    private static readonly Answer Hang = new Hanging();
    private static readonly Answer Fault = new Faulting();
    private static readonly Answer Unavailable = new Reply(HttpStatusCode.ServiceUnavailable, "{}");
    private static readonly Answer NoEvents = new Reply(HttpStatusCode.OK, """{"events":[]}""");
    private static readonly DateTime LiveStart = new(2026, 11, 7, 0, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime MlbOriginalStart = new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime HistoricalStart = new(2026, 9, 13, 17, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PregameNow = new(2026, 10, 4, 19, 56, 0, TimeSpan.Zero);
    private const string FirstGame = "401872985";
    private const string SecondGame = "401872986";
    private const string ThirdGame = "401872987";
    private const string FourthGame = "401872988";
    private static readonly Answer KalshiCandles = new Reply(HttpStatusCode.OK, """
        {"candlesticks":[
            {"end_period_ts":1791730800,"price":{"close_dollars":"0.4000"},"volume_fp":"5"},
            {"end_period_ts":1791734400,"price":{"close_dollars":"0.6000"},"volume_fp":"5"}]}
        """);
    private static readonly Answer CoreOdds = new Reply(HttpStatusCode.OK,
        """{"items":[{"homeTeamOdds":{"close":{"moneyLine":{"american":"-150"}}},"awayTeamOdds":{"close":{"moneyLine":{"american":"+130"}}}}]}""");

    private static readonly Dictionary<string, (Answer Answer, Action<IServiceCollection> Configure, Type Exception)> Faults = new()
    {
        ["own fault"] = (Fault, _ => { }, typeof(InvalidOperationException)),
        ["client deadline"] = (Hang, services => services.ConfigureAll<HttpClientFactoryOptions>(options =>
            options.HttpClientActions.Add(client => client.Timeout = TimeSpan.FromMilliseconds(200))), typeof(TaskCanceledException)),
    };

    private static readonly Dictionary<string, BackfillOptions> BackfillRuns = new()
    {
        ["default"] = OneWeek(kalshi: true, skipExisting: true),
        ["update"] = OneWeek(kalshi: true),
        ["dry run"] = OneWeek(kalshi: true, skipExisting: true, dryRun: true),
    };

    private static readonly Dictionary<string, Answer> UnusableAnswers = new()
    {
        ["not found"] = NotFound,
        ["bad request"] = new Reply(HttpStatusCode.BadRequest, "{}"),
        ["malformed body"] = new Reply(HttpStatusCode.OK, """{"markets":["""),
    };

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
        ["Markets"] = (LiveOdds("nba", "BOS", "NYK", LiveStart, "Failed to fetch markets from Kalshi for {Sport}"),
            uri => uri.AbsolutePath == "/trade-api/v2/markets" ? Hang : NotFound),
        ["MarketByTicker"] = (LiveOdds("nba", "BOS", "NYK", LiveStart, "Failed to fetch Kalshi market {Ticker}"),
            uri => uri.AbsolutePath == "/trade-api/v2/markets" ? EmptyCatalog : Hang),
        ["WorldCupMarkets"] = (new(typeof(KalshiOddsService), "Failed to fetch World Cup markets from Kalshi",
                async (services, ct) => await services.GetRequiredService<KalshiOddsService>().GetWorldCupThreeWayLiveOddsAsync("USA", "MEX", LiveStart, ct)),
            uri => uri.AbsolutePath == "/trade-api/v2/markets" ? Hang : NotFound),
        ["SameDayMlbSchedule"] = (LiveOdds("mlb", "CHW", "CLE", MlbOriginalStart, "Could not verify MLB schedule for {EventTicker}"),
            uri => uri.AbsolutePath switch
            {
                "/trade-api/v2/markets" => new Reply(HttpStatusCode.OK, MlbScheduleChangeCatalog()),
                "/trade-api/v2/milestones" => Hang,
                _ => NotFound
            }),
        ["MarketTickerSearch"] = (new(typeof(KalshiOddsService), "Failed to fetch markets from Kalshi for {Sport}",
                async (services, ct) => await services.GetRequiredService<KalshiOddsService>().FindMarketTickerAsync("nba", "BOS", "NYK", LiveStart, ct)),
            uri => uri.AbsolutePath == "/trade-api/v2/markets" ? Hang : NotFound),
        ["Historical"] = (Historical, _ => Hang),
    };

    public static TheoryData<string> ScoreboardNames => [.. Scoreboards.Keys];

    public static TheoryData<string> KalshiFetchNames => [.. KalshiFetches.Keys];

    public static TheoryData<string, string> FetchFaults()
    {
        var cases = new TheoryData<string, string>();
        foreach (var name in Scoreboards.Keys.Concat(KalshiFetches.Keys))
            foreach (var fault in Faults.Keys)
                cases.Add(name, fault);
        return cases;
    }

    public static TheoryData<string, string> UnusableKalshiAnswers()
    {
        var cases = new TheoryData<string, string>();
        foreach (var name in KalshiFetches.Keys)
            foreach (var answer in UnusableAnswers.Keys)
                cases.Add(name, answer);
        return cases;
    }

    public static TheoryData<string, HttpStatusCode, string> UnreadableScoreboards()
    {
        var cases = new TheoryData<string, HttpStatusCode, string>();
        foreach (var name in Scoreboards.Keys)
        {
            cases.Add(name, HttpStatusCode.OK, """{"events":[""");
            cases.Add(name, HttpStatusCode.OK, "");
            cases.Add(name, HttpStatusCode.OK, """{"events":{}}""");
            cases.Add(name, HttpStatusCode.OK, """{"leagues":5}""");
            cases.Add(name, HttpStatusCode.OK, "[]");
            cases.Add(name, HttpStatusCode.NotFound, "{}");
        }
        cases.Add("EspnOdds", HttpStatusCode.OK, "null");
        cases.Add("EspnOdds", HttpStatusCode.OK, "{}");
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
    [MemberData(nameof(FetchFaults))]
    public async Task Own_fault_or_client_deadline_at_a_fetch_is_sent_at_error_once_per_failure(string name, string fault)
    {
        var (fetch, answer) = Scoreboards.TryGetValue(name, out var scoreboard) ? (scoreboard, _ => Hang) : KalshiFetches[name];
        var (faultAnswer, configure, exception) = Faults[fault];
        await using var app = new OddsApp(uri => answer(uri) is Hanging ? faultAnswer : answer(uri), services =>
        {
            PinClockForMlb(services);
            configure(services);
        });

        await fetch.Run(app.Services, CancellationToken.None);

        var failures = app.FailuresFrom(fetch.Service);
        Assert.NotEmpty(failures);
        Assert.All(failures, failure => Assert.Equal((LogLevel.Error, exception), (failure.Level, failure.Exception!.GetType())));
        var sent = await app.SentEventsAsync();
        Assert.Equal(failures.Count, sent.Count);
        Assert.All(sent, reported =>
        {
            Assert.Equal((fetch.Service.FullName, "error", fetch.Template), (reported.Logger, reported.Level, reported.Template));
            Assert.Contains(reported.Exceptions, e => e.Type == exception.FullName);
        });
    }

    [Theory]
    [MemberData(nameof(UnusableKalshiAnswers))]
    public async Task Unusable_kalshi_answer_sends_no_event(string name, string unusable)
    {
        var (fetch, answer) = KalshiFetches[name];
        await using var app = new OddsApp(uri => answer(uri) is Hanging ? UnusableAnswers[unusable] : answer(uri), PinClockForMlb);

        await fetch.Run(app.Services, CancellationToken.None);

        Assert.DoesNotContain(app.FailuresFrom(fetch.Service), failure => failure.Level > LogLevel.Warning);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Theory]
    [InlineData("own fault")]
    [InlineData("client deadline")]
    [InlineData("bad request")]
    [InlineData("malformed body")]
    public async Task Kalshi_pregame_fetch_sends_own_faults_and_deadlines_at_error_and_nothing_for_unusable_answers(string kind)
    {
        var isFault = Faults.TryGetValue(kind, out var fault);
        var kalshi = isFault ? fault.Answer : UnusableAnswers[kind];
        var board = BackfillBoard();
        await using var database = await OddsDatabase.StartAsync();
        await using var app = new OddsApp(uri => uri.AbsolutePath.StartsWith("/trade-api/", StringComparison.Ordinal) ? kalshi
            : new Reply(HttpStatusCode.OK, uri.Query.Contains("dates=20261011", StringComparison.Ordinal) ? board : """{"events":[]}"""), services =>
        {
            database.Use()(services);
            PinClock(services, new FakeTimeProvider(PregameNow));
            if (isFault) fault.Configure(services);
        });

        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PregameOddsService>().RefreshPregameOddsForSportAsync("nfl", CancellationToken.None);

        Assert.Contains(app.Provider.Requests, uri => uri.AbsolutePath.StartsWith("/trade-api/", StringComparison.Ordinal));
        var sent = await app.SentEventsAsync();
        if (isFault) AssertSentOnceAtError(sent, typeof(PregameOddsService), PregameKalshiTemplate, fault.Exception);
        else
        {
            Assert.DoesNotContain(app.FailuresFrom(typeof(PregameOddsService)), failure => failure.Level > LogLevel.Warning);
            Assert.Empty(sent);
        }
    }

    [Theory]
    [InlineData("catalog that never ends", 1, 2)]
    [InlineData("own fault", 3, 3)]
    public async Task Kalshi_pregame_catalog_that_never_ends_is_sent_once_per_pass_and_own_faults_once_per_game(string kind, int events, int requests)
    {
        var kalshi = kind == "own fault" ? Fault : new Reply(HttpStatusCode.OK, """{"markets":[],"cursor":"again"}""");
        var board = BackfillBoard("401872986", "401872987");
        await using var database = await OddsDatabase.StartAsync();
        await using var app = new OddsApp(uri => uri.AbsolutePath.StartsWith("/trade-api/", StringComparison.Ordinal) ? kalshi
                : new Reply(HttpStatusCode.OK, uri.Query.Contains("dates=20261011", StringComparison.Ordinal) ? board : """{"events":[]}"""), services =>
        {
            database.Use()(services);
            PinClock(services, new FakeTimeProvider(PregameNow));
        });

        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PregameOddsService>().RefreshPregameOddsForSportAsync("nfl", CancellationToken.None);

        Assert.Equal(requests, app.Provider.Requests.Count(uri => uri.AbsolutePath.StartsWith("/trade-api/", StringComparison.Ordinal)));
        var sent = await app.SentEventsAsync();
        Assert.Equal(events, sent.Count);
        Assert.All(sent, reported => Assert.Equal((typeof(PregameOddsService).FullName, "error", PregameKalshiTemplate),
            (reported.Logger, reported.Level, reported.Template)));
    }

    [Fact]
    public async Task Kalshi_pregame_catalog_that_never_ends_leaves_other_sports_to_kalshi()
    {
        var board = BackfillBoard();
        await using var database = await OddsDatabase.StartAsync();
        await using var app = new OddsApp(uri => uri.AbsolutePath.StartsWith("/trade-api/", StringComparison.Ordinal)
                ? uri.Query.Contains("series_ticker=KXNFLGAME", StringComparison.Ordinal)
                    ? new Reply(HttpStatusCode.OK, """{"markets":[],"cursor":"again"}""") : EmptyCatalog
                : new Reply(HttpStatusCode.OK, uri.Query.Contains("dates=20261011", StringComparison.Ordinal) ? board : """{"events":[]}"""), services =>
        {
            database.Use()(services);
            PinClock(services, new FakeTimeProvider(PregameNow));
        });

        using var scope = app.Services.CreateScope();
        var pregame = scope.ServiceProvider.GetRequiredService<PregameOddsService>();
        await pregame.RefreshPregameOddsForSportAsync("nfl", CancellationToken.None);
        await pregame.RefreshPregameOddsForSportAsync("nba", CancellationToken.None);

        Assert.Contains(app.Provider.Requests, uri => uri.Query.Contains("series_ticker=KXNBAGAME", StringComparison.Ordinal));
        Assert.Single(await app.SentEventsAsync());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task Espn_body_that_is_not_a_scoreboard_is_a_warning_and_the_pregame_pass_carries_on(string body)
    {
        var board = BackfillBoard();
        await using var database = await OddsDatabase.StartAsync();
        await using var app = new OddsApp(uri => uri.AbsolutePath.StartsWith("/trade-api/", StringComparison.Ordinal) ? EmptyCatalog
            : new Reply(HttpStatusCode.OK, uri.Query.Contains("dates=20261011", StringComparison.Ordinal) ? board : body), services =>
        {
            database.Use()(services);
            PinClock(services, new FakeTimeProvider(PregameNow));
        });

        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PregameOddsService>().RefreshPregameOddsForSportAsync("nfl", CancellationToken.None);

        Assert.Contains(app.Provider.Requests, uri => uri.AbsolutePath.StartsWith("/trade-api/", StringComparison.Ordinal));
        var failures = app.FailuresFrom(typeof(EspnOddsService));
        Assert.Equal(7, failures.Count);
        Assert.All(failures, failure => Assert.Equal(LogLevel.Warning, failure.Level));
        Assert.Empty(await app.SentEventsAsync());
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
    public async Task Kalshi_history_outage_latch_leaves_own_faults_at_error()
    {
        var requests = 0;
        await using var app = new OddsApp(_ => Interlocked.Increment(ref requests) <= 2 * StandardAttempts ? Unavailable : Fault);

        var kalshi = app.Services.GetRequiredService<KalshiOddsService>();
        foreach (var _ in Enumerable.Range(0, 3))
            Assert.Empty(await kalshi.GetHistoricalOddsAsync("nfl", "PHI", "WSH", HistoricalStart, 60, CancellationToken.None));

        Assert.Equal([LogLevel.Error, LogLevel.Warning, LogLevel.Error], app.FailuresFrom(typeof(KalshiOddsService)).Select(f => f.Level));
        Assert.Equal([typeof(HttpRequestException).FullName, typeof(InvalidOperationException).FullName],
            (await app.SentEventsAsync()).Select(reported => reported.Exceptions[^1].Type));
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
    public async Task Kalshi_history_refusal_after_an_outage_is_still_sent_and_ends_the_backfill_pass()
    {
        var requests = 0;
        await using var app = new OddsApp(_ => Interlocked.Increment(ref requests) <= StandardAttempts ? Unavailable : new Reply(HttpStatusCode.Forbidden, "{}"));

        var kalshi = app.Services.GetRequiredService<KalshiOddsService>();
        foreach (var _ in Enumerable.Range(0, 3))
            Assert.Empty(await kalshi.GetHistoricalOddsAsync("nfl", "PHI", "WSH", HistoricalStart, 60, CancellationToken.None));

        Assert.Equal(StandardAttempts + 1, app.Provider.Requests.Count);
        Assert.Equal([LogLevel.Error, LogLevel.Error], app.FailuresFrom(typeof(KalshiOddsService)).Select(f => f.Level));
        Assert.Equal([typeof(HttpRequestException).FullName, "backend.Services.KalshiAccessRefusedException"],
            (await app.SentEventsAsync()).Select(reported => reported.Exceptions[^1].Type));
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

    [Theory]
    [InlineData("own fault")]
    [InlineData("client deadline")]
    public async Task Backfill_core_odds_own_fault_or_client_deadline_is_sent_once_at_error(string kind)
    {
        var (answer, configure, exception) = Faults[kind];
        var board = BackfillBoard();
        await using var database = await OddsDatabase.StartAsync();
        await using var app = new OddsApp(uri => uri.Host == "sports.core.api.espn.com" ? answer : new Reply(HttpStatusCode.OK, board), services =>
        {
            database.Use()(services);
            configure(services);
        });

        var result = await app.Services.GetRequiredService<NflBackfillService>().BackfillSeasonAsync(2026, OneWeek(), CancellationToken.None);

        Assert.Equal((1, 0), (result.GamesCreated, result.Errors.Count));
        Assert.Equal(LogLevel.Error, Assert.Single(app.FailuresFrom(typeof(NflBackfillService))).Level);
        AssertSentOnceAtError(await app.SentEventsAsync(), typeof(NflBackfillService), CoreOddsTemplate, exception);
    }

    [Fact]
    public async Task Backfill_core_odds_outage_latch_leaves_own_faults_at_error()
    {
        var board = BackfillBoard("401872986", "401872987");
        await using var database = await OddsDatabase.StartAsync();
        await using var app = new OddsApp(uri => uri.Host != "sports.core.api.espn.com" ? new Reply(HttpStatusCode.OK, board)
            : uri.AbsolutePath.Contains("/events/401872987/", StringComparison.Ordinal) ? Fault : Unavailable, database.Use());

        var result = await app.Services.GetRequiredService<NflBackfillService>().BackfillSeasonAsync(2026, OneWeek(), CancellationToken.None);

        Assert.Equal((3, 0), (result.GamesCreated, result.Errors.Count));
        Assert.Equal([LogLevel.Error, LogLevel.Warning, LogLevel.Error], app.FailuresFrom(typeof(NflBackfillService)).Select(f => f.Level));
        Assert.Equal([typeof(HttpRequestException).FullName, typeof(InvalidOperationException).FullName],
            (await app.SentEventsAsync()).Select(reported => reported.Exceptions[^1].Type));
    }

    [Fact]
    public async Task Backfill_core_odds_outage_is_sent_once_per_pass()
    {
        var board = BackfillBoard("401872986");
        await using var database = await OddsDatabase.StartAsync();
        await using var app = new OddsApp(uri => uri.Host == "sports.core.api.espn.com" ? Unavailable : new Reply(HttpStatusCode.OK, board),
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

    [Theory]
    [InlineData("FROM pregame_odds", "Failed to fetch pregame odds for game {GameId}", 1, 0)]
    [InlineData("FROM odds_snapshots", "Failed to fetch Kalshi historical odds for game {GameId}", 1, 0)]
    [InlineData("FROM games", "Error processing game {GameId}", 0, 1)]
    [InlineData("INSERT INTO games", "Error processing {SeasonType} week {Week}", 1, 1)]
    public async Task Backfill_own_fault_is_sent_once_at_error_and_the_backfill_carries_on(string sql, string template, int created, int errors)
    {
        var board = BackfillBoard();
        await using var database = await OddsDatabase.StartAsync();
        await using var app = new OddsApp(uri => uri.AbsolutePath.EndsWith("/scoreboard", StringComparison.Ordinal) ? new Reply(HttpStatusCode.OK, board) : NotFound,
            database.Use(interceptor: new FaultingCommand(sql)));

        var result = await app.Services.GetRequiredService<NflBackfillService>().BackfillSeasonAsync(2026, OneWeek(kalshi: true), CancellationToken.None);

        Assert.Equal((created, errors), (result.GamesCreated, result.Errors.Count));
        AssertSentOnceAtError(await app.SentEventsAsync(), typeof(NflBackfillService), template, typeof(InvalidOperationException));
    }

    [Fact]
    public async Task Backfill_own_fault_after_the_weeks_is_sent_once_at_error()
    {
        await using var app = new OddsApp(_ => NoEvents, services =>
        {
            services.RemoveAll<RetrospectiveEloService>();
            services.AddScoped<RetrospectiveEloService>(_ => throw new InvalidOperationException("synthetic Elo service failure"));
        });

        var result = await app.Services.GetRequiredService<NflBackfillService>().BackfillSeasonAsync(2026,
            new() { SeasonType = 2, StartWeek = 19, CalculateElo = true, KalshiCandlestickInterval = 60 }, CancellationToken.None);

        Assert.StartsWith("Fatal error: ", Assert.Single(result.Errors));
        AssertSentOnceAtError(await app.SentEventsAsync(), typeof(NflBackfillService), "Backfill failed with error", typeof(InvalidOperationException));
    }

    [Theory]
    [InlineData("new games")]
    [InlineData("existing games")]
    public async Task Backfill_failed_game_save_leaks_no_rows_into_the_next_game(string games)
    {
        var existing = games == "existing games";
        await using var database = await OddsDatabase.StartTimescaleAsync();
        if (existing) await database.SeedAsync(BackfillGame(FirstGame, weekFields: true), BackfillGame(SecondGame, weekFields: true));
        await database.ExecuteAsync($"""
            CREATE FUNCTION fail_backfill_save() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'synthetic save failure for %', NEW.game_id;
            END $$;
            CREATE TRIGGER fail_backfill_save BEFORE INSERT ON odds_snapshots FOR EACH ROW
                WHEN (NEW.game_id = '{FirstGame}') EXECUTE FUNCTION fail_backfill_save();
            """);
        await using var app = new OddsApp(BackfillProviders(BackfillBoard(SecondGame), NotFound), database.Use());

        var result = await app.Services.GetRequiredService<NflBackfillService>().BackfillSeasonAsync(2026,
            OneWeek(kalshi: true, skipExisting: existing), CancellationToken.None);

        Assert.StartsWith($"Game {FirstGame}:", Assert.Single(result.Errors));
        Assert.Equal([$"odds {SecondGame} kalshi_backfill 2"], await SnapshotRowsAsync(database));
        Assert.Equal(existing ? new[] { FirstGame, SecondGame } : [SecondGame], await database.TextAsync("SELECT id FROM games ORDER BY id"));
        AssertSentOnceAtError(await app.SentEventsAsync(), typeof(NflBackfillService), "Error processing game {GameId}", typeof(PostgresException));
    }

    [Fact]
    public async Task Backfill_writes_each_game_in_its_own_transaction()
    {
        await using var database = await StartThreeGameBackfillAsync();
        await database.ExecuteAsync("""
            CREATE TABLE backfill_writes (seq bigserial PRIMARY KEY, tx bigint NOT NULL, tbl text NOT NULL, game_id text NOT NULL);
            CREATE FUNCTION audit_backfill_write() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                INSERT INTO backfill_writes (tx, tbl, game_id) VALUES (txid_current(), TG_ARGV[0], to_jsonb(NEW) ->> TG_ARGV[1]);
                RETURN NULL;
            END $$;
            CREATE TRIGGER audit_games AFTER INSERT OR UPDATE ON games FOR EACH ROW EXECUTE FUNCTION audit_backfill_write('games', 'id');
            CREATE TRIGGER audit_pregame_odds AFTER INSERT OR UPDATE ON pregame_odds FOR EACH ROW EXECUTE FUNCTION audit_backfill_write('pregame_odds', 'game_id');
            CREATE TRIGGER audit_odds_snapshots AFTER INSERT OR UPDATE ON odds_snapshots FOR EACH ROW EXECUTE FUNCTION audit_backfill_write('odds_snapshots', 'game_id');
            """);
        await using var app = new OddsApp(BackfillProviders(BackfillBoard(SecondGame, ThirdGame, FourthGame), CoreOdds), database.Use());

        var result = await app.Services.GetRequiredService<NflBackfillService>().BackfillSeasonAsync(2026,
            OneWeek(kalshi: true, skipExisting: true), CancellationToken.None);

        Assert.Empty(result.Errors);
        var writes = (await database.QueryAsync("SELECT tx, tbl, game_id FROM backfill_writes ORDER BY seq"))
            .Select(row => (Tx: (long)row[0], Table: (string)row[1], Game: (string)row[2])).ToArray();
        Assert.Equal([
                $"{FirstGame} games", $"{FirstGame} odds_snapshots",
                $"{ThirdGame} games", $"{ThirdGame} odds_snapshots", $"{ThirdGame} pregame_odds",
                $"{FourthGame} games", $"{FourthGame} odds_snapshots", $"{FourthGame} pregame_odds"
            ], writes.Select(w => $"{w.Game} {w.Table}").Distinct().Order());
        Assert.All(writes.GroupBy(w => w.Tx), transaction => Assert.Single(transaction.Select(w => w.Game).Distinct()));
        Assert.All(writes.GroupBy(w => w.Game), game => Assert.Single(game.Select(w => w.Tx).Distinct()));
        Assert.All(writes.GroupBy(w => w.Tx), transaction =>
            Assert.DoesNotContain(transaction.SkipWhile(w => w.Table == "games"), w => w.Table == "games"));
    }

    [Theory]
    [InlineData("default", new[] { 1, 0, 2, 1, 4 }, new[]
    {
        "game 401872985 2026 2 1 Week 1", "game 401872986 2026 2 1 Week 1", "game 401872987 2026 2 1 Week 1",
        "pregame 401872987 espn_backfill",
        "odds 401872985 kalshi_backfill 2", "odds 401872986 kalshi_backfill 1", "odds 401872987 kalshi_backfill 2"
    })]
    [InlineData("update", new[] { 1, 2, 0, 3, 4 }, new[]
    {
        "game 401872985 2026 2 1 Week 1", "game 401872986 2026 2 1 Week 1", "game 401872987 2026 2 1 Week 1",
        "pregame 401872985 espn_backfill", "pregame 401872986 espn_backfill", "pregame 401872987 espn_backfill",
        "odds 401872985 kalshi_backfill 2", "odds 401872986 kalshi_backfill 1", "odds 401872987 kalshi_backfill 2"
    })]
    [InlineData("dry run", new[] { 1, 0, 2, 0, 0 }, new[]
    {
        "game 401872985 2026 2 1 Week 1", "game 401872986 2026 2 1 Week 1",
        "odds 401872986 kalshi_backfill 1"
    })]
    public async Task Backfill_counters_and_rows_stay_the_same(string run, int[] counters, string[] rows)
    {
        await using var database = await StartThreeGameBackfillAsync();
        await using var app = new OddsApp(BackfillProviders(BackfillBoard(SecondGame, ThirdGame), CoreOdds), database.Use());

        var result = await app.Services.GetRequiredService<NflBackfillService>().BackfillSeasonAsync(2026, BackfillRuns[run], CancellationToken.None);

        Assert.Empty(result.Errors);
        Assert.Equal(counters, new[] { result.GamesCreated, result.GamesUpdated, result.GamesSkipped, result.PregameOddsCreated, result.KalshiOddsSnapshotsCreated });
        Assert.Equal(rows, (string[])[
            .. await database.TextAsync("SELECT concat_ws(' ', 'game', id, season_year, season_type, week_number, week_label) FROM games ORDER BY id"),
            .. await database.TextAsync("SELECT concat_ws(' ', 'pregame', game_id, source) FROM pregame_odds ORDER BY game_id"),
            .. await SnapshotRowsAsync(database)
        ]);
    }

    [Fact]
    public async Task Scrape_loop_shutdown_mid_fetch_ends_quietly()
    {
        var clock = new FakeTimeProvider(Now);
        await using var app = new OddsApp(_ => Hang);
        var loop = LoopOn(app, clock);

        await loop.StartAsync(CancellationToken.None);
        await AdvanceUntilAsync(clock, () => app.Provider.HangStarted.IsCompleted, TimeSpan.FromSeconds(30));
        await loop.StopAsync(CancellationToken.None);

        Assert.True(loop.ExecuteTask!.IsCanceled);
        Assert.DoesNotContain(app.Logs, l => l.Level >= LogLevel.Warning && l.Category.StartsWith("backend", StringComparison.Ordinal));
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Scrape_loop_own_fault_outside_a_step_is_sent_once_per_cycle_and_the_loop_runs_again()
    {
        var clock = new FakeTimeProvider(Now);
        await using var app = new OddsApp(_ => NoEvents, services =>
        {
            services.RemoveAll<IGameUpdateService>();
            services.AddSingleton<IGameUpdateService>(_ => throw new InvalidOperationException("synthetic update service failure"));
        });
        var loop = LoopOn(app, clock);
        var failures = () => app.FailuresFrom(typeof(OddsScrapeBackgroundService));

        await loop.StartAsync(CancellationToken.None);
        await AdvanceUntilAsync(clock, () => failures().Count(f => f.Message == "Error during live odds scraping cycle") >= 2, TimeSpan.FromSeconds(30));
        await loop.StopAsync(CancellationToken.None);

        Assert.All(failures(), failure => Assert.Equal((LogLevel.Error, typeof(InvalidOperationException)), (failure.Level, failure.Exception!.GetType())));
        Assert.Contains(failures(), failure => failure.Message == "Error during pregame odds refresh cycle");
        Assert.True(loop.ExecuteTask!.IsCanceled);
        AssertEachLoopFailureSentOnceAtError(failures(), await app.SentEventsAsync());
    }

    [Fact]
    public async Task Scrape_loop_own_fault_inside_a_step_is_sent_once_per_failure_and_the_loop_runs_again()
    {
        var clock = new FakeTimeProvider(Now);
        await using var app = new OddsApp(_ => NoEvents, services =>
        {
            services.RemoveAll<ExcitementDbContext>();
            services.AddScoped<ExcitementDbContext>(_ => throw new InvalidOperationException("synthetic database context failure"));
        });
        var loop = LoopOn(app, clock);
        var failures = () => app.FailuresFrom(typeof(OddsScrapeBackgroundService));

        await loop.StartAsync(CancellationToken.None);
        await AdvanceUntilAsync(clock, () => failures().Count(f => f.Message == "Failed to scrape live odds for {Sport}") >= 10, TimeSpan.FromSeconds(30));
        await loop.StopAsync(CancellationToken.None);

        Assert.All(failures(), failure => Assert.Equal((LogLevel.Error, typeof(InvalidOperationException)), (failure.Level, failure.Exception!.GetType())));
        Assert.Contains(failures(), failure => failure.Message == "Failed to refresh pregame odds");
        Assert.True(loop.ExecuteTask!.IsCanceled);
        AssertEachLoopFailureSentOnceAtError(failures(), await app.SentEventsAsync());
    }

    [Fact]
    public async Task Scrape_loop_database_failure_is_sent_once_per_sport_pass_and_the_loop_runs_again()
    {
        var clock = new FakeTimeProvider(Now);
        await using var database = await OddsDatabase.StartAsync();
        await using var tableLock = await database.HoldLockAsync("LOCK TABLE games IN ACCESS EXCLUSIVE MODE");
        var useDatabase = database.Use(LockTimeout);
        await using var app = new OddsApp(_ => NoEvents, useDatabase);
        var loop = LoopOn(app, clock);
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

    private static Fetch LiveOdds(string sport, string home, string away, DateTime start, string template) => new(typeof(KalshiOddsService), template,
        async (services, ct) => await services.GetRequiredService<KalshiOddsService>().GetLiveOddsAsync(sport, home, away, start, ct));

    private static BackfillOptions OneWeek(bool kalshi = false, bool skipExisting = false, bool dryRun = false) => new()
    {
        SeasonType = 2,
        StartWeek = 1,
        EndWeek = 1,
        SkipExisting = skipExisting,
        DryRun = dryRun,
        BackfillKalshiOdds = kalshi,
        KalshiCandlestickInterval = 60
    };

    private static Func<Uri, Answer> BackfillProviders(string board, Answer coreOdds) => uri =>
        uri.AbsolutePath.EndsWith("/scoreboard", StringComparison.Ordinal) ? new Reply(HttpStatusCode.OK, board)
        : uri.AbsolutePath.EndsWith("/candlesticks", StringComparison.Ordinal) ? KalshiCandles
        : uri.Host == "sports.core.api.espn.com" ? coreOdds
        : NotFound;

    private static Game BackfillGame(string id, bool weekFields) => new()
    {
        Id = id,
        Sport = "nfl",
        HomeTeam = "Pittsburgh Steelers",
        AwayTeam = "Indianapolis Colts",
        HomeTeamAbbreviation = "PIT",
        AwayTeamAbbreviation = "IND",
        ScheduledStart = new DateTime(2026, 10, 11, 17, 0, 0, DateTimeKind.Utc),
        SeasonYear = weekFields ? 2026 : null,
        SeasonType = weekFields ? 2 : null,
        WeekNumber = weekFields ? 1 : null,
        WeekLabel = weekFields ? "Week 1" : null
    };

    private static async Task<OddsDatabase> StartThreeGameBackfillAsync()
    {
        var database = await OddsDatabase.StartTimescaleAsync();
        await database.SeedAsync(BackfillGame(FirstGame, weekFields: false), BackfillGame(SecondGame, weekFields: true), new OddsSnapshot
        {
            GameId = SecondGame,
            Timestamp = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc),
            HomeMoneyline = -110,
            AwayMoneyline = -110,
            HomeWinProbability = 0.5,
            AwayWinProbability = 0.5,
            Source = "kalshi_backfill"
        });
        return database;
    }

    private static Task<List<string>> SnapshotRowsAsync(OddsDatabase database) => database.TextAsync(
        "SELECT concat_ws(' ', 'odds', game_id, source, count(*)) FROM odds_snapshots GROUP BY game_id, source ORDER BY game_id, source");

    private static string BackfillBoard(params string[] moreGameIds)
    {
        var board = JsonNode.Parse(ProHypeTests.Fixture("nfl-pregame-401872985").GetProperty("board").GetProperty("body").GetRawText())!;
        var events = board["events"]!.AsArray();
        var first = events[0]!;
        foreach (var id in moreGameIds)
        {
            var game = first.DeepClone();
            game["id"] = id;
            events.Add(game);
        }
        return board.ToJsonString();
    }

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

    private static OddsScrapeBackgroundService LoopOn(OddsApp app, TimeProvider clock) =>
        ActivatorUtilities.CreateInstance<OddsScrapeBackgroundService>(app.Services, clock);

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

    private static void AssertEachLoopFailureSentOnceAtError(IReadOnlyCollection<LoggedEntry> failures, IReadOnlyCollection<SentEvent> sent)
    {
        Assert.Equal(failures.Count, sent.Count);
        Assert.All(sent, reported =>
        {
            Assert.Equal((typeof(OddsScrapeBackgroundService).FullName, "error"), (reported.Logger, reported.Level));
            Assert.Contains(reported.Exceptions, e => e.Type == typeof(InvalidOperationException).FullName);
        });
        Assert.Equal(failures.Select(f => f.Message).Order(), sent.Select(reported => reported.Template).Order());
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

    private sealed record Faulting : Answer;

    private sealed record SentEvent(string? Logger, string? Level, string? Template, (string? Type, string? Value)[] Exceptions);

    private sealed record LoggedEntry(string Category, LogLevel Level, Exception? Exception, string? Message);

    private sealed class OddsDatabase(PostgreSqlContainer container) : IAsyncDisposable
    {
        public static Task<OddsDatabase> StartAsync() => StartAsync(IsolatedPostgreSql.CreateContainer(), _ => Task.CompletedTask);

        public static Task<OddsDatabase> StartTimescaleAsync() => StartAsync(
            IsolatedPostgreSql.CreateBuilder(ListingWorkload.Image()).WithCommand(ListingWorkload.ServerSettings).Build(), ListingWorkload.PrepareTemplateAsync);

        private static async Task<OddsDatabase> StartAsync(PostgreSqlContainer container, Func<string, Task> prepare)
        {
            var database = new OddsDatabase(container);
            await container.StartAsync();
            await using (var db = database.Context())
                await db.Database.EnsureCreatedAsync();
            await prepare(container.GetConnectionString());
            await using (var db = database.Context())
                await TestSchema.CreateAsync(db.Database);
            return database;
        }

        public async Task SeedAsync(params object[] rows)
        {
            await using var db = Context();
            db.AddRange(rows);
            await db.SaveChangesAsync();
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(container.GetConnectionString());
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<List<object[]>> QueryAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(container.GetConnectionString());
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<object[]>();
            while (await reader.ReadAsync())
            {
                var row = new object[reader.FieldCount];
                reader.GetValues(row);
                rows.Add(row);
            }
            return rows;
        }

        public async Task<List<string>> TextAsync(string sql) => [.. (await QueryAsync(sql)).Select(row => (string)row[0])];

        private ExcitementDbContext Context() =>
            new(new DbContextOptionsBuilder<ExcitementDbContext>().UseNpgsql(container.GetConnectionString()).Options);

        public Action<IServiceCollection> Use(string options = "", IInterceptor? interceptor = null) => services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
            services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
            services.AddDbContext<ExcitementDbContext>(builder =>
            {
                builder.UseNpgsql(container.GetConnectionString() + options);
                if (interceptor is not null) builder.AddInterceptors(interceptor);
            });
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

    private sealed class FaultingCommand(string sql) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) => Check(command, result);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) => Check(command, result);

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default) => Check(command, result);

        private ValueTask<T> Check<T>(DbCommand command, T result) => command.CommandText.Contains(sql, StringComparison.Ordinal)
            ? throw new InvalidOperationException($"synthetic fault on {sql}")
            : ValueTask.FromResult(result);
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
            var reply = answer(request.RequestUri!);
            if (reply is Faulting) throw new InvalidOperationException("synthetic fault below the provider fetch");
            if (reply is Reply status)
                return new HttpResponseMessage(status.Status) { Content = new StringContent(status.Body) };
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
