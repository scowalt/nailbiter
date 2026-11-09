using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using backend.Data;
using backend.Models;
using backend.Services;
using backend.Services.Espn;
using backend.Services.Nascar;
using backend.Services.PlayoffStatus;
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
public sealed class ProviderFailureReportingTests
{
    private const int StandardAttempts = 4;
    private static readonly DateTime LiveStart = new(2026, 11, 7, 0, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime MlbOriginalStart = new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset MlbScheduleChecked = new(MlbOriginalStart.AddHours(1.25));

    [Fact]
    public async Task Provider_failure_its_caller_treats_as_best_effort_sends_no_event()
    {
        await using var app = new ReportingApp();

        using var scope = app.Services.CreateScope();
        var feed = await scope.ServiceProvider.GetRequiredService<NascarLiveFeedService>().GetLiveFeedAsync(CancellationToken.None);

        Assert.Null(feed);
        Assert.Equal(StandardAttempts, app.Provider.Failures);
        var warning = Assert.Single(app.Logs, l => l.Category == typeof(NascarLiveFeedService).FullName);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.IsType<HttpRequestException>(warning.Exception);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Provider_failure_its_caller_logs_as_an_error_is_sent_once_by_that_caller()
    {
        await using var app = new ReportingApp();

        using var scope = app.Services.CreateScope();
        var refreshed = await scope.ServiceProvider.GetRequiredService<EspnStandingsService>()
            .RefreshNflStandingsAsync(CancellationToken.None);

        Assert.Equal(0, refreshed);
        await AssertEachFailureSentOnceAsync<EspnStandingsService>(app, "Failed to refresh {Sport} standings from ESPN");
    }

    [Fact]
    public async Task Provider_failure_that_opens_the_circuit_is_still_sent_by_its_error_caller()
    {
        await using var app = new ReportingApp(configure: services => services.ConfigureAll<HttpStandardResilienceOptions>(options =>
            options.CircuitBreaker.MinimumThroughput = StandardAttempts));

        using var scope = app.Services.CreateScope();
        var refreshed = await scope.ServiceProvider.GetRequiredService<EspnStandingsService>()
            .RefreshNflStandingsAsync(CancellationToken.None);

        Assert.Equal(0, refreshed);
        Assert.Contains(app.Logs, l => l.Category == "Polly" && l.Message.Contains("OnCircuitOpened", StringComparison.Ordinal));
        await AssertEachFailureSentOnceAsync<EspnStandingsService>(app, "Failed to refresh {Sport} standings from ESPN");
    }

    [Fact]
    public async Task Kalshi_live_markets_failure_is_sent_once()
    {
        await using var app = new ReportingApp(uri => uri.AbsolutePath == "/trade-api/v2/markets" ? Unreachable : NotFound());

        var odds = await app.Get<KalshiOddsService>().GetLiveOddsAsync("nba", "BOS", "NYK", LiveStart, CancellationToken.None);

        Assert.Null(odds);
        await AssertEachFailureSentOnceAsync<KalshiOddsService>(app, "Failed to fetch markets from Kalshi for {Sport}");
    }

    [Fact]
    public async Task Kalshi_ticker_fallback_failures_are_each_sent_once()
    {
        await using var app = new ReportingApp(uri => uri.AbsolutePath == "/trade-api/v2/markets" ? Ok("""{"markets":[],"cursor":""}""") : Unreachable);

        var odds = await app.Get<KalshiOddsService>().GetLiveOddsAsync("nba", "BOS", "NYK", LiveStart, CancellationToken.None);

        Assert.Null(odds);
        await AssertEachFailureSentOnceAsync<KalshiOddsService>(app, "Failed to fetch Kalshi market {Ticker}", failures: 4);
    }

    [Fact]
    public async Task Kalshi_live_markets_server_errors_after_retries_are_sent_once()
    {
        await using var app = new ReportingApp(uri => uri.AbsolutePath == "/trade-api/v2/markets"
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : NotFound());

        var odds = await app.Get<KalshiOddsService>().GetLiveOddsAsync("nba", "BOS", "NYK", LiveStart, CancellationToken.None);

        Assert.Null(odds);
        await AssertEachFailureSentOnceAsync<KalshiOddsService>(app, "Failed to fetch markets from Kalshi for {Sport}");
    }

    [Fact]
    public async Task Kalshi_live_markets_timeouts_after_retries_are_sent_once()
    {
        await using var app = new ReportingApp(uri => uri.AbsolutePath == "/trade-api/v2/markets" ? Hang : NotFound(),
            services => services.ConfigureAll<HttpStandardResilienceOptions>(options =>
                options.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(50)));

        var odds = await app.Get<KalshiOddsService>().GetLiveOddsAsync("nba", "BOS", "NYK", LiveStart, CancellationToken.None);

        Assert.Null(odds);
        await AssertEachFailureSentOnceAsync<KalshiOddsService>(app, "Failed to fetch markets from Kalshi for {Sport}", typeof(TimeoutRejectedException));
    }

    [Fact]
    public async Task Kalshi_live_markets_request_refused_by_an_open_circuit_is_sent()
    {
        var circuitBreak = TimeSpan.FromMilliseconds(500);
        await using var app = new ReportingApp(uri => uri.AbsolutePath == "/trade-api/v2/markets" ? Unreachable : NotFound(),
            services =>
            {
                PinClock(services, new DateTimeOffset(LiveStart));
                services.ConfigureAll<HttpStandardResilienceOptions>(options =>
                {
                    options.CircuitBreaker.MinimumThroughput = StandardAttempts;
                    options.CircuitBreaker.BreakDuration = circuitBreak;
                });
            });
        var kalshi = app.Get<KalshiOddsService>();

        Assert.Null(await kalshi.GetLiveOddsAsync("nba", "BOS", "NYK", LiveStart, CancellationToken.None));
        await Task.Delay(circuitBreak * 2);
        Assert.Null(await kalshi.GetLiveOddsAsync("nba", "BOS", "NYK", LiveStart, CancellationToken.None));

        var sent = await app.SentEventsAsync();
        Assert.Equal(StandardAttempts, app.Provider.Failures);
        Assert.All(sent, reported => Assert.Equal(("error", typeof(KalshiOddsService).FullName), (reported.Level, reported.Logger)));
        Assert.Equal(2, sent.Count(reported => reported.Template == "Failed to fetch markets from Kalshi for {Sport}"));
        Assert.Contains(sent, reported => reported.Template == "Failed to fetch markets from Kalshi for {Sport}"
            && reported.ExceptionTypes[^1] == typeof(BrokenCircuitException).FullName);
    }

    [Fact]
    public async Task Kalshi_live_markets_unreadable_body_stays_a_warning()
    {
        await using var app = new ReportingApp(uri => uri.AbsolutePath == "/trade-api/v2/markets"
            ? Ok("<html>not json</html>") : NotFound());

        var odds = await app.Get<KalshiOddsService>().GetLiveOddsAsync("nba", "BOS", "NYK", LiveStart, CancellationToken.None);

        Assert.Null(odds);
        Assert.Single(app.Provider.Requests, u => u.AbsolutePath == "/trade-api/v2/markets");
        Assert.Equal(LogLevel.Warning, Assert.Single(app.Logs, l => l.Category == typeof(KalshiOddsService).FullName && l.Exception is not null).Level);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Theory]
    [MemberData(nameof(KalshiAnswers))]
    public async Task Kalshi_live_markets_refusal_is_sent_once_and_ends_the_lookup(HttpStatusCode status)
    {
        await using var app = new ReportingApp(_ => Status(status));

        var odds = await app.Get<KalshiOddsService>().GetLiveOddsAsync("nba", "BOS", "NYK", LiveStart, CancellationToken.None);

        Assert.Null(odds);
        await AssertKalshiAnswerReportedAsync<KalshiOddsService>(app, status, "Failed to fetch markets from Kalshi for {Sport}");
    }

    [Theory]
    [MemberData(nameof(KalshiAnswers))]
    public async Task Kalshi_ticker_fallback_refusal_is_sent_once_and_ends_the_lookup(HttpStatusCode status)
    {
        await using var app = new ReportingApp(uri => uri.AbsolutePath == "/trade-api/v2/markets"
            ? Ok("""{"markets":[],"cursor":""}""") : Status(status));

        var odds = await app.Get<KalshiOddsService>().GetLiveOddsAsync("nba", "BOS", "NYK", LiveStart, CancellationToken.None);

        Assert.Null(odds);
        await AssertKalshiAnswerReportedAsync<KalshiOddsService>(app, status, "Failed to fetch Kalshi market {Ticker}", requestsBeforeRefusal: 1);
    }

    [Theory]
    [MemberData(nameof(KalshiAnswers))]
    public async Task Kalshi_world_cup_markets_refusal_is_sent_once(HttpStatusCode status)
    {
        await using var app = new ReportingApp(_ => Status(status));

        var odds = await app.Get<KalshiOddsService>().GetWorldCupThreeWayLiveOddsAsync("USA", "MEX", LiveStart, CancellationToken.None);

        Assert.Null(odds);
        await AssertKalshiAnswerReportedAsync<KalshiOddsService>(app, status, "Failed to fetch World Cup markets from Kalshi");
    }

    [Theory]
    [MemberData(nameof(KalshiAnswers))]
    public async Task Kalshi_same_day_mlb_schedule_check_refusal_is_sent_once(HttpStatusCode status)
    {
        await using var app = new ReportingApp(uri => uri.AbsolutePath switch
        {
            "/trade-api/v2/markets" => Ok(MlbScheduleChangeCatalog()),
            "/trade-api/v2/milestones" => Status(status),
            _ => NotFound()
        }, services => PinClock(services, MlbScheduleChecked));

        var odds = await app.Get<KalshiOddsService>().GetLiveOddsAsync("mlb", "CHW", "CLE", MlbOriginalStart, CancellationToken.None);

        Assert.Null(odds);
        await AssertKalshiAnswerReportedAsync<KalshiOddsService>(app, status, "Could not verify MLB schedule for {EventTicker}", requestsBeforeRefusal: 1);
    }

    [Fact]
    public async Task Kalshi_refusal_ends_the_live_scrape_pass()
    {
        await using var app = new ReportingApp(_ => Status(HttpStatusCode.Forbidden));
        var kalshi = app.Get<KalshiOddsService>();

        Assert.Null(await kalshi.GetLiveOddsAsync("nba", "BOS", "NYK", LiveStart, CancellationToken.None));
        Assert.Null(await kalshi.GetLiveOddsAsync("nba", "LAL", "GSW", LiveStart, CancellationToken.None));
        Assert.Null(await kalshi.GetLiveOddsAsync("nhl", "BOS", "NYR", LiveStart, CancellationToken.None));
        Assert.Null(await kalshi.GetWorldCupThreeWayLiveOddsAsync("USA", "MEX", LiveStart, CancellationToken.None));
        Assert.Null(await kalshi.GetWorldCupAdvanceLiveOddsAsync("USA", "MEX", LiveStart, CancellationToken.None));

        Assert.Single(app.Provider.Requests);
        Assert.Single(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Kalshi_refusal_does_not_outlive_its_scrape_pass()
    {
        await using var app = new ReportingApp(_ => Status(HttpStatusCode.Forbidden));

        foreach (var _ in Enumerable.Range(0, 2))
        {
            using var scope = app.Services.CreateScope();
            var kalshi = scope.ServiceProvider.GetRequiredService<KalshiOddsService>();
            Assert.Null(await kalshi.GetLiveOddsAsync("nba", "BOS", "NYK", LiveStart, CancellationToken.None));
        }

        Assert.Equal(2, app.Provider.Requests.Count(KalshiRequest));
        Assert.Equal(2, (await app.SentEventsAsync()).Count);
    }

    [Fact]
    public async Task Kalshi_world_cup_markets_failure_is_sent_once()
    {
        await using var app = new ReportingApp();

        var odds = await app.Get<KalshiOddsService>().GetWorldCupThreeWayLiveOddsAsync("USA", "MEX", LiveStart, CancellationToken.None);

        Assert.Null(odds);
        await AssertEachFailureSentOnceAsync<KalshiOddsService>(app, "Failed to fetch World Cup markets from Kalshi");
    }

    [Fact]
    public async Task Kalshi_same_day_mlb_schedule_check_failure_is_sent_once()
    {
        await using var app = new ReportingApp(uri => uri.AbsolutePath switch
        {
            "/trade-api/v2/markets" => Ok(MlbScheduleChangeCatalog()),
            "/trade-api/v2/milestones" => Unreachable,
            _ => NotFound()
        }, services => PinClock(services, MlbScheduleChecked));

        var odds = await app.Get<KalshiOddsService>().GetLiveOddsAsync("mlb", "CHW", "CLE", MlbOriginalStart, CancellationToken.None);

        Assert.Null(odds);
        await AssertEachFailureSentOnceAsync<KalshiOddsService>(app, "Could not verify MLB schedule for {EventTicker}");
    }

    [Fact]
    public async Task Kalshi_pregame_catalog_failure_is_sent_once()
    {
        var board = ProHypeTests.Fixture("nfl-pregame-401872985").GetProperty("board").GetProperty("body").GetRawText();
        await using var app = await ReportingApp.WithDatabaseAsync(uri => uri.AbsolutePath.EndsWith("/scoreboard", StringComparison.Ordinal)
            ? Ok(uri.Query.Contains("dates=20261011", StringComparison.Ordinal) ? board : """{"events":[]}""")
            : Unreachable, services => PinClock(services, new DateTimeOffset(2026, 10, 4, 19, 56, 0, TimeSpan.Zero)));

        using var scope = app.Services.CreateScope();
        var updated = await scope.ServiceProvider.GetRequiredService<PregameOddsService>()
            .RefreshPregameOddsForSportAsync("nfl", CancellationToken.None);

        Assert.Equal(0, updated);
        await AssertEachFailureSentOnceAsync<PregameOddsService>(app, "Failed to fetch Kalshi pregame odds for {Game}");
    }

    [Theory]
    [MemberData(nameof(KalshiAnswers))]
    public async Task Kalshi_pregame_catalog_refusal_is_sent_once(HttpStatusCode status)
    {
        await using var app = await PregameAppAsync(status, games: 1);

        using var scope = app.Services.CreateScope();
        var updated = await scope.ServiceProvider.GetRequiredService<PregameOddsService>()
            .RefreshPregameOddsForSportAsync("nfl", CancellationToken.None);

        Assert.Equal(0, updated);
        await AssertKalshiAnswerReportedAsync<PregameOddsService>(app, status, "Failed to fetch Kalshi pregame odds for {Game}");
    }

    [Fact]
    public async Task Kalshi_refusal_ends_the_pregame_pass()
    {
        await using var app = await PregameAppAsync(HttpStatusCode.Forbidden, games: 3);

        using var scope = app.Services.CreateScope();
        var updated = await scope.ServiceProvider.GetRequiredService<PregameOddsService>()
            .RefreshPregameOddsForSportAsync("nfl", CancellationToken.None);

        Assert.Equal(0, updated);
        Assert.Single(app.Provider.Requests, KalshiRequest);
        Assert.Single(await app.SentEventsAsync());
    }

    [Theory]
    [MemberData(nameof(KalshiAnswers))]
    public async Task College_kalshi_quote_refusal_is_sent_once(HttpStatusCode status)
    {
        await using var app = new ReportingApp(_ => Status(status));

        var probability = await app.Get<CollegeKalshiClient>().ObserveProbabilityAsync(
            new CollegeMarketPair("KXNCAAFGAME-26NOV07AAABBB-AAA", "KXNCAAFGAME-26NOV07AAABBB-BBB"), LiveStart, CancellationToken.None);

        Assert.Null(probability);
        await AssertKalshiAnswerReportedAsync<CollegeKalshiClient>(app, status, "College game quotes unavailable for {Ticker}");
    }

    [Fact]
    public async Task Kalshi_refusal_ends_the_college_quote_pass()
    {
        await using var app = new ReportingApp(_ => Status(HttpStatusCode.Forbidden));
        var kalshi = app.Get<CollegeKalshiClient>();

        Assert.Null(await kalshi.ObserveProbabilityAsync(
            new CollegeMarketPair("KXNCAAFGAME-26NOV07AAABBB-AAA", "KXNCAAFGAME-26NOV07AAABBB-BBB"), LiveStart, CancellationToken.None));
        Assert.Null(await kalshi.ObserveProbabilityAsync(
            new CollegeMarketPair("KXNCAAFGAME-26NOV07CCCDDD-CCC", "KXNCAAFGAME-26NOV07CCCDDD-DDD"), LiveStart, CancellationToken.None));

        Assert.Single(app.Provider.Requests);
        Assert.Single(await app.SentEventsAsync());
    }

    [Theory]
    [MemberData(nameof(KalshiAnswers))]
    public async Task College_listings_refusal_is_sent_once(HttpStatusCode status)
    {
        await using var app = await CollegeAppAsync(uri => uri.AbsolutePath == "/trade-api/v2/markets", status);

        Assert.Equal(0, await RunCollegeAsync(app));
        await AssertKalshiAnswerReportedAsync<CollegeFootballService>(app, status, "College listings unavailable; refreshing known games only");
    }

    [Fact]
    public async Task College_kalshi_quote_failure_is_sent_once()
    {
        await using var app = new ReportingApp();

        var probability = await app.Get<CollegeKalshiClient>().ObserveProbabilityAsync(
            new CollegeMarketPair("KXNCAAFGAME-26NOV07AAABBB-AAA", "KXNCAAFGAME-26NOV07AAABBB-BBB"), LiveStart, CancellationToken.None);

        Assert.Null(probability);
        await AssertEachFailureSentOnceAsync<CollegeKalshiClient>(app, "College game quotes unavailable for {Ticker}");
    }

    [Fact]
    public async Task Kalshi_playoff_probabilities_failure_is_sent_once()
    {
        await using var app = new ReportingApp();

        using var scope = app.Services.CreateScope();
        var refreshed = await scope.ServiceProvider.GetRequiredService<KalshiPlayoffRatingsService>()
            .RefreshAsync("nhl", 2027, CancellationToken.None);

        Assert.Equal(0, refreshed);
        await AssertEachFailureSentOnceAsync<KalshiPlayoffRatingsService>(app,
            "Failed to refresh Kalshi {Sport} playoff probabilities for season {Season}");
    }

    [Theory]
    [MemberData(nameof(KalshiAnswers))]
    public async Task Kalshi_playoff_probabilities_refusal_is_sent_once(HttpStatusCode status)
    {
        await using var app = new ReportingApp(_ => Status(status));

        using var scope = app.Services.CreateScope();
        var refreshed = await scope.ServiceProvider.GetRequiredService<KalshiPlayoffRatingsService>()
            .RefreshAsync("nhl", 2027, CancellationToken.None);

        Assert.Equal(0, refreshed);
        await AssertKalshiAnswerReportedAsync<KalshiPlayoffRatingsService>(app, status,
            "Failed to refresh Kalshi {Sport} playoff probabilities for season {Season}");
    }

    [Theory]
    [MemberData(nameof(KalshiAnswers))]
    public async Task Kalshi_playoff_archive_refusal_is_sent_once(HttpStatusCode status)
    {
        await using var app = await PlayoffArchiveAppAsync(() => Status(status));

        using var scope = app.Services.CreateScope();
        var refreshed = await scope.ServiceProvider.GetRequiredService<KalshiPlayoffRatingsService>()
            .RefreshAsync("nhl", 2027, CancellationToken.None);

        Assert.Equal(31, refreshed);
        await AssertKalshiAnswerReportedAsync<KalshiPlayoffRatingsService>(app, status,
            "Failed to recover archived {Sport} playoff finals for season {Season}; missing nonfinal probabilities are cleared without advancing their timestamps",
            requestsBeforeRefusal: 1);
    }

    [Fact]
    public async Task Kalshi_refusal_ends_the_playoff_refresh_pass()
    {
        await using var app = await ReportingApp.WithDatabaseAsync(_ => Status(HttpStatusCode.Forbidden),
            services => PinClock(services, new DateTimeOffset(LiveStart)));

        using var scope = app.Services.CreateScope();
        var refreshed = await scope.ServiceProvider.GetRequiredService<KalshiPlayoffRatingsService>()
            .RefreshCurrentAsync(CancellationToken.None);

        Assert.Equal(0, refreshed);
        Assert.Single(app.Provider.Requests);
        Assert.Single(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Kalshi_playoff_archive_failure_is_sent_once()
    {
        await using var app = await PlayoffArchiveAppAsync(() => Unreachable);

        using var scope = app.Services.CreateScope();
        var refreshed = await scope.ServiceProvider.GetRequiredService<KalshiPlayoffRatingsService>()
            .RefreshAsync("nhl", 2027, CancellationToken.None);

        Assert.Equal(31, refreshed);
        await AssertEachFailureSentOnceAsync<KalshiPlayoffRatingsService>(app,
            "Failed to recover archived {Sport} playoff finals for season {Season}; missing nonfinal probabilities are cleared without advancing their timestamps");
    }

    [Fact]
    public async Task Kalshi_playoff_probabilities_own_fault_is_sent_once_at_error()
    {
        await using var app = new ReportingApp(_ => throw new InvalidOperationException("Synthetic fault below the fetch"));

        using var scope = app.Services.CreateScope();
        var refreshed = await scope.ServiceProvider.GetRequiredService<KalshiPlayoffRatingsService>()
            .RefreshAsync("nhl", 2027, CancellationToken.None);

        Assert.Equal(0, refreshed);
        AssertSentOnceAtError<KalshiPlayoffRatingsService>(await app.SentEventsAsync(),
            "Failed to refresh Kalshi {Sport} playoff probabilities for season {Season}", typeof(InvalidOperationException));
    }

    [Theory]
    [InlineData("own fault")]
    [InlineData("""{"markets":null,"cursor":null}""")]
    public async Task Kalshi_playoff_archive_own_fault_or_broken_bound_is_sent_once_at_error(string archive)
    {
        await using var app = await PlayoffArchiveAppAsync(() => archive == "own fault"
            ? throw new InvalidOperationException("Synthetic fault below the fetch") : Ok(archive));

        using var scope = app.Services.CreateScope();
        var refreshed = await scope.ServiceProvider.GetRequiredService<KalshiPlayoffRatingsService>()
            .RefreshAsync("nhl", 2027, CancellationToken.None);

        Assert.Equal(31, refreshed);
        AssertSentOnceAtError<KalshiPlayoffRatingsService>(await app.SentEventsAsync(),
            "Failed to recover archived {Sport} playoff finals for season {Season}; missing nonfinal probabilities are cleared without advancing their timestamps",
            typeof(InvalidOperationException));
    }

    [Fact]
    public async Task Unreadable_kalshi_playoff_answers_are_warnings_without_an_event()
    {
        await using var app = await PlayoffArchiveAppAsync(() => Ok("<html>maintenance</html>"));

        using var scope = app.Services.CreateScope();
        var refreshed = await scope.ServiceProvider.GetRequiredService<KalshiPlayoffRatingsService>()
            .RefreshAsync("nhl", 2027, CancellationToken.None);
        var unreadableEvent = await scope.ServiceProvider.GetRequiredService<KalshiPlayoffRatingsService>()
            .RefreshAsync("nba", 2027, CancellationToken.None);

        Assert.Equal((31, 0), (refreshed, unreadableEvent));
        var failures = app.Logs.Where(l => l.Category == typeof(KalshiPlayoffRatingsService).FullName && l.Exception is not null).ToArray();
        Assert.Equal(2, failures.Length);
        Assert.All(failures, failure => Assert.Equal((LogLevel.Warning, true), (failure.Level, failure.Exception is JsonException)));
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Nhl_playoff_model_refresh_failure_is_sent_once()
    {
        await using var app = new ReportingApp(configure: services => services.Configure<PlayoffRatingsOptions>(options =>
        {
            options.NhlModelMode = "fallback";
            options.NhlModelRefreshEnabled = true;
            options.ApprovedSeasons = [2027];
        }));

        using var scope = app.Services.CreateScope();
        var refreshed = await scope.ServiceProvider.GetRequiredService<PlayoffStatusService>()
            .RefreshNhlRatingsForSeasonAsync(2027, CancellationToken.None);

        Assert.Equal(0, refreshed);
        await AssertEachFailureSentOnceAsync<PlayoffStatusService>(app, "Failed NHL PlayoffStatus model refresh for season {Season}");
    }

    [Fact]
    public async Task Pro_competition_evidence_failure_is_sent_once()
    {
        await using var app = await ReportingApp.WithDatabaseAsync(_ => Unreachable, services => PinClock(services, new DateTimeOffset(LiveStart)));
        await app.SeedAsync(new Game
        {
            Id = "401900001", Sport = "nba", Status = "scheduled", SeasonYear = 2027, SeasonType = 2,
            HomeTeam = "Boston Celtics", AwayTeam = "New York Knicks", HomeTeamAbbreviation = "BOS", AwayTeamAbbreviation = "NY",
            ScheduledStart = LiveStart.AddDays(1)
        });

        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ProCompetitionEvidenceService>().RefreshAsync(CancellationToken.None);

        await AssertEachFailureSentOnceAsync<ProCompetitionEvidenceService>(app,
            "Pro context request failed for {Sport}/{Season}; previous timestamps preserved");
    }

    [Fact]
    public async Task College_roster_failure_is_sent_once()
    {
        await using var app = await CollegeAppAsync(uri => uri.AbsolutePath.EndsWith("/powerindex", StringComparison.Ordinal));
        await app.SeedAsync(new CollegeTeam
        {
            Season = 2026, EspnId = "2483", Abbreviation = "ORE", Name = "Oregon",
            FpiUpdatedAt = CollegeNow.UtcDateTime.AddDays(-1), FetchedAt = CollegeNow.UtcDateTime.AddDays(-1)
        });

        Assert.Equal(0, await RunCollegeAsync(app));
        await AssertEachFailureSentOnceAsync<CollegeFootballService>(app,
            "College roster refresh unavailable; retaining verified season membership and expiring old metric evidence");
    }

    [Fact]
    public async Task College_rankings_failure_is_sent_once()
    {
        await using var app = await CollegeAppAsync(uri => uri.AbsolutePath.EndsWith("/rankings", StringComparison.Ordinal));

        Assert.Equal(0, await RunCollegeAsync(app));
        await AssertEachFailureSentOnceAsync<CollegeFootballService>(app, "College rankings unavailable; existing evidence will expire normally");
    }

    [Fact]
    public async Task College_listings_failure_is_sent_once()
    {
        await using var app = await CollegeAppAsync(uri => uri.AbsolutePath == "/trade-api/v2/markets");

        Assert.Equal(0, await RunCollegeAsync(app));
        await AssertEachFailureSentOnceAsync<CollegeFootballService>(app, "College listings unavailable; refreshing known games only");
    }

    [Fact]
    public async Task College_event_failure_is_sent_once()
    {
        await using var app = await CollegeAppAsync(uri => uri.AbsolutePath.EndsWith("/summary", StringComparison.Ordinal));
        await app.SeedAsync(new Game
        {
            Id = "401900002", Sport = "cfb", Status = "scheduled", SeasonYear = 2026, SeasonType = 2,
            HomeTeam = "Oregon", AwayTeam = "Rice", HomeTeamAbbreviation = "ORE", AwayTeamAbbreviation = "RICE",
            ScheduledStart = CollegeNow.UtcDateTime.AddDays(-3)
        });

        Assert.Equal(0, await RunCollegeAsync(app));
        await AssertEachFailureSentOnceAsync<CollegeFootballService>(app, "College event {Event} missing; preserving stored state");
    }

    [Fact]
    public async Task College_event_the_provider_no_longer_has_stays_a_warning()
    {
        await using var app = await CollegeAppAsync(_ => false);
        await app.SeedAsync(new Game
        {
            Id = "401900002", Sport = "cfb", Status = "scheduled", SeasonYear = 2026, SeasonType = 2,
            HomeTeam = "Oregon", AwayTeam = "Rice", HomeTeamAbbreviation = "ORE", AwayTeamAbbreviation = "RICE",
            ScheduledStart = CollegeNow.UtcDateTime.AddDays(-3)
        });

        Assert.Equal(0, await RunCollegeAsync(app));
        var missing = Assert.Single(app.Logs, l => l.Category == typeof(CollegeFootballService).FullName && l.Exception is not null);
        Assert.Equal(LogLevel.Warning, missing.Level);
        Assert.Equal(HttpStatusCode.NotFound, Assert.IsType<HttpRequestException>(missing.Exception).StatusCode);
        Assert.Empty(await app.SentEventsAsync());
    }

    public static TheoryData<HttpStatusCode> KalshiAnswers => [HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound];

    private static bool KalshiRequest(Uri uri) => uri.AbsolutePath.StartsWith("/trade-api/", StringComparison.Ordinal);

    private static async Task AssertKalshiAnswerReportedAsync<TCaller>(ReportingApp app, HttpStatusCode status, string template, int requestsBeforeRefusal = 0)
    {
        var sent = await app.SentEventsAsync();
        Assert.Equal(0, app.Provider.Failures);
        if (status == HttpStatusCode.NotFound)
        {
            Assert.Empty(sent);
            return;
        }
        var reported = Assert.Single(sent);
        Assert.Equal((typeof(TCaller).FullName, "error", template, "backend.Services.KalshiAccessRefusedException"),
            (reported.Logger, reported.Level, reported.Template, reported.ExceptionTypes[^1]));
        Assert.Equal(requestsBeforeRefusal + 1, app.Provider.Requests.Count(KalshiRequest));
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

    private static Task<ReportingApp> PregameAppAsync(HttpStatusCode kalshi, int games)
    {
        var board = JsonNode.Parse(ProHypeTests.Fixture("nfl-pregame-401872985").GetProperty("board").GetProperty("body").GetRawText())!;
        var events = board["events"]!.AsArray();
        for (var i = 1; i < games; i++)
        {
            var copy = events[0]!.DeepClone();
            copy["id"] = $"40187298{5 + i}";
            events.Add(copy);
        }
        return ReportingApp.WithDatabaseAsync(uri => uri.AbsolutePath.EndsWith("/scoreboard", StringComparison.Ordinal)
            ? Ok(uri.Query.Contains("dates=20261011", StringComparison.Ordinal) ? board.ToJsonString() : """{"events":[]}""")
            : Status(kalshi), services => PinClock(services, new DateTimeOffset(2026, 10, 4, 19, 56, 0, TimeSpan.Zero)));
    }

    private static Task<ReportingApp> PlayoffArchiveAppAsync(Func<HttpResponseMessage?> archive)
    {
        var live = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NhlSeeding", "qualification.json")))!;
        live["event"]!["markets"]!.AsArray().RemoveAt(0);
        return ReportingApp.WithDatabaseAsync(uri => uri.AbsolutePath == "/trade-api/v2/events/KXNHLPLAYOFF-27"
            ? Ok(live.ToJsonString()) : archive(), services => PinClock(services, new DateTimeOffset(LiveStart)));
    }

    private static readonly DateTimeOffset CollegeNow = new(2026, 9, 16, 18, 0, 0, TimeSpan.Zero);

    private static Task<ReportingApp> CollegeAppAsync(Func<Uri, bool> failing, HttpStatusCode? answer = null) =>
        ReportingApp.WithDatabaseAsync(uri => failing(uri) ? (answer is { } status ? Status(status) : Unreachable) : uri switch
        {
            { AbsolutePath: var path } when path.EndsWith("/powerindex", StringComparison.Ordinal) => Ok(CollegeRoster),
            { AbsolutePath: var path } when path.EndsWith("/rankings", StringComparison.Ordinal) => Ok(CollegeApPoll),
            { AbsolutePath: var path } when path.EndsWith("/scoreboard", StringComparison.Ordinal) => Ok(CollegeScoreboard),
            { Host: "sports.core.api.espn.com", AbsolutePath: var path } when path.EndsWith("/events", StringComparison.Ordinal) =>
                Ok("""{"count":0,"pageCount":0,"pageIndex":0,"pageSize":25,"items":[]}"""),
            { AbsolutePath: "/trade-api/v2/markets" } => Ok("""{"markets":[],"cursor":""}"""),
            _ => NotFound()
        }, services => PinClock(services, CollegeNow));

    private static async Task<int> RunCollegeAsync(ReportingApp app)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CollegeFootballService>().RunAsync(false, true, CancellationToken.None);
    }

    private const string CollegeScoreboard = """
        {"leagues":[{"season":{"year":2026},"calendar":[{"value":"2","startDate":"2026-08-22T07:00Z"}]}],"events":[]}
        """;

    private const string CollegeApPoll = """
        {"rankings":[{"id":"1","type":"ap","season":{"year":2026},"lastUpdated":"2026-09-13T21:19Z","ranks":[{"current":5,"team":{"id":"2483"}}]}]}
        """;

    private const string CollegeRoster = """
        {"requestedSeason":{"year":2026},"currentSeason":{"year":2026},"currentValues":{"season":2026,"conference":"80"},
         "pagination":{"page":1,"pages":1,"count":2},"lastUpdated":"2026-09-16T08:00Z",
         "categories":[{"name":"fpi","names":["fpirank","fpi"]}],
         "teams":[{"team":{"id":"2483","abbreviation":"ORE","displayName":"Oregon"},"categories":[{"name":"fpi","values":[2.0,25.0]}]},
                  {"team":{"id":"242","abbreviation":"RICE","displayName":"Rice"},"categories":[{"name":"fpi","values":[90.0,-12.0]}]}]}
        """;

    private static Task AssertEachFailureSentOnceAsync<TCaller>(ReportingApp app, string template, int failures = 1) =>
        AssertEachFailureSentOnceAsync<TCaller>(app, template, typeof(HttpRequestException), failures);

    private static async Task AssertEachFailureSentOnceAsync<TCaller>(ReportingApp app, string template, Type exception, int failures = 1)
    {
        var sent = await app.SentEventsAsync();
        Assert.Equal(failures * StandardAttempts, app.Provider.Failures);
        Assert.Equal(failures, sent.Count);
        Assert.All(sent, reported =>
        {
            Assert.Equal(typeof(TCaller).FullName, reported.Logger);
            Assert.Equal("error", reported.Level);
            Assert.Equal(template, reported.Template);
            Assert.Equal(exception.FullName, reported.ExceptionTypes[^1]);
        });
    }

    private static void AssertSentOnceAtError<TCaller>(IReadOnlyCollection<SentEvent> sent, string template, Type exception)
    {
        var reported = Assert.Single(sent);
        Assert.Equal((typeof(TCaller).FullName, "error", template), (reported.Logger, reported.Level, reported.Template));
        Assert.Contains(exception.FullName, reported.ExceptionTypes);
    }

    private static void PinClock(IServiceCollection services, DateTimeOffset now)
    {
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(now));
    }

    private static HttpResponseMessage? Unreachable => null;

    private static readonly HttpResponseMessage Hang = new(HttpStatusCode.Continue);

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage NotFound() => Status(HttpStatusCode.NotFound);

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent("{}") };

    private sealed record SentEvent(string? Logger, string? Level, string? Template, string?[] ExceptionTypes);

    private sealed record LoggedEntry(string Category, LogLevel Level, Exception? Exception, string Message);

    private sealed class ReportingApp : IAsyncDisposable
    {
        private readonly AppFactory entry = new();
        private PostgreSqlContainer? database;
        private readonly WebApplicationFactory<Program> app;
        private readonly SentTransport transport = new();
        private readonly LogRecorder logs = new();

        public ReportingApp(Func<Uri, HttpResponseMessage?>? respond = null, Action<IServiceCollection>? configure = null)
        {
            Provider = new SyntheticProvider(respond ?? (_ => Unreachable));
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

        public static async Task<ReportingApp> WithDatabaseAsync(Func<Uri, HttpResponseMessage?> respond, Action<IServiceCollection>? configure = null)
        {
            var database = IsolatedPostgreSql.CreateContainer();
            await database.StartAsync();
            var app = new ReportingApp(respond, services =>
            {
                services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
                services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
                services.AddDbContext<ExcitementDbContext>(options => options.UseNpgsql(database.GetConnectionString()));
                configure?.Invoke(services);
            }) { database = database };
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.EnsureCreatedAsync();
            return app;
        }

        public SyntheticProvider Provider { get; }

        public IServiceProvider Services => app.Services;

        public T Get<T>() where T : notnull => app.Services.GetRequiredService<T>();

        public IEnumerable<LoggedEntry> Logs => logs.Entries;

        public async Task SeedAsync(object entity)
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            db.Add(entity);
            await db.SaveChangesAsync();
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
            if (database is not null) await database.DisposeAsync();
        }
    }

    private sealed class SyntheticProvider(Func<Uri, HttpResponseMessage?> respond) : HttpMessageHandler
    {
        private int failures;

        public ConcurrentQueue<Uri> Requests { get; } = new();

        public int Failures => failures;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!);
            var response = respond(request.RequestUri!);
            if (response is { StatusCode: < HttpStatusCode.InternalServerError } && response != Hang) return response;
            Interlocked.Increment(ref failures);
            if (response == Hang) await Task.Delay(Timeout.Infinite, cancellationToken);
            return response ?? throw new HttpRequestException("synthetic provider connection failure");
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
