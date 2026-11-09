using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using backend.Data;
using backend.Models;
using backend.Scoring;
using backend.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace backend.tests;

public sealed class ProHypeAcceptanceTests : IAsyncLifetime
{
    private static readonly TimeSpan HangDeadline = TimeSpan.FromSeconds(60);
    private readonly PostgreSqlContainer database = IsolatedPostgreSql.CreateContainer();
    private readonly AppFactory entry = new();
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(ProHypeTests.Now));
    private readonly ControlledOutbound outbound = new();
    private readonly PublicationGate publication = new();
    private DbContextOptions<ExcitementDbContext> options = null!;
    private WebApplicationFactory<Program>? app;
    private HttpClient client = null!;

    private sealed class ControlledOutbound : IHttpMessageHandlerBuilderFilter
    {
        public int Failures;
        public int Unexpected;
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            builder.PrimaryHandler = new Handler(this);
        };
        private sealed class Handler(ControlledOutbound owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/teams", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref owner.Failures);
                    return Task.FromException<HttpResponseMessage>(new TaskCanceledException("Controlled provider transport timeout"));
                }
                Interlocked.Increment(ref owner.Unexpected);
                throw new InvalidOperationException("Unexpected acceptance provider request: " + request.RequestUri);
            }
        }
    }

    private sealed class PublicationGate : DbTransactionInterceptor
    {
        private readonly AsyncTestGate gate = new();
        public TaskCompletionSource Entered => gate.Entered;
        public void Arm() => gate.Arm();
        public void Release() => gate.Release();
        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken ct = default)
        {
            await gate.EnterAsync(ct);
            return result;
        }
    }

    public async Task InitializeAsync()
    {
        await database.StartAsync();
        options = new DbContextOptionsBuilder<ExcitementDbContext>().UseNpgsql(database.GetConnectionString()).Options;
        await Change(async db =>
        {
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "035_ProHypeLifecycle.sql")));
        });
    }

    private async Task Change(Func<ExcitementDbContext, Task> action)
    {
        await using var db = new ExcitementDbContext(options);
        await action(db);
        await db.SaveChangesAsync();
    }

    private void Start()
    {
        app = entry.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
            services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
            services.AddDbContext<ExcitementDbContext>(o => o.UseNpgsql(database.GetConnectionString()).AddInterceptors(publication));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IHttpMessageHandlerBuilderFilter>(outbound);
        }));
        client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
    }

    private async Task Restart()
    {
        client.Dispose();
        await app!.DisposeAsync();
        Start();
    }

    private async Task<Game> Seed(string sport = "nhl", int phase = 2)
    {
        var game = ProHypeTests.Game(sport);
        game.Id = "acceptance-game";
        game.WeekNumber = 4;
        game.SeasonType = phase;
        game.ScheduledStart = ProHypeTests.Now.AddDays(3).AddHours(4.5);
        var evidence = ProHypeContinuityInputs.Evidence(game);
        if (phase == 3) { evidence.Round = 4; evidence.HomeElimination = evidence.AwayElimination = true; }
        if (phase == 5) evidence.PlayIn = "knockout";
        game.ProEvidenceJson = JsonSerializer.Serialize(evidence);
        await Change(db =>
        {
            db.Games.Add(game);
            db.PregameOdds.Add(ProHypeTests.Odds(game));
            var ranks = ProHypeTests.Ranks(game);
            foreach (var rank in ranks.Where(r => r.RankPublishedAt.HasValue)) rank.RankPublishedAt = ProHypeTests.Now.AddDays(-6);
            db.TeamRatings.AddRange(ranks);
            if (phase == 2)
                foreach (var team in new[] { game.HomeTeamAbbreviation, game.AwayTeamAbbreviation })
                    db.TeamRatings.Add(new TeamRating { Sport = sport, TeamAbbreviation = team, Season = game.SeasonYear!.Value,
                        SourceSeason = game.SeasonYear, Source = "kalshi", PlayoffProbability = .5, FetchedAt = ProHypeTests.Now });
            return Task.CompletedTask;
        });
        return game;
    }

    private async Task Capture()
    {
        using var scope = app!.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().CaptureAsync();
    }

    private async Task<ProHypeResolution> Resolve(Game game)
    {
        using var scope = app!.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().ResolveAsync(game.Id);
    }

    private static void NoDiagnostics(string output) => ProHypeOutputAssertions.NoDiagnostics(output, "model_projection");

    private async Task<IDocument> Page(string path, bool update = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (update) request.Headers.Add("X-Nailbiter-Live-Snapshot", "1");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        NoDiagnostics(html);
        return await new HtmlParser().ParseDocumentAsync(html);
    }

    private static string LeaguePath(Game game) => game.Sport == "nfl"
        ? $"/nfl?week=4&season={game.SeasonYear}&type={game.SeasonType}"
        : $"/{game.Sport}?date={game.ScheduledStart:yyyy-MM-dd}";

    private async Task<ProCurrentHypeEvaluation> Current(Game game)
    {
        using var scope = app!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var authoritative = await db.Games.AsNoTracking().SingleAsync(g => g.Id == game.Id);
        var completed = await db.Games.Where(g => g.Sport == game.Sport && g.SeasonYear == game.SeasonYear
            && g.SeasonType == game.SeasonType && g.Status == "final").Select(g => g.Id).ToArrayAsync();
        return scope.ServiceProvider.GetRequiredService<HypeScoreService>().EvaluateFromEvidence(authoritative,
            await db.TeamRatings.ToArrayAsync(), await db.PregameOdds.SingleAsync(), completed, clock.GetUtcNow().UtcDateTime);
    }

    private async Task WholePreview(Game game, ProHypeResolution expected)
    {
        for (var viewer = 0; viewer < 2; viewer++)
        {
            using var independentViewer = app!.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            foreach (var update in new[] { false, true })
                foreach (var path in new[] { "/game/" + game.Id, "/", LeaguePath(game) })
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, path);
                    if (update) request.Headers.Add("X-Nailbiter-Live-Snapshot", "1");
                    using var response = await independentViewer.SendAsync(request);
                    response.EnsureSuccessStatusCode();
                    var html = await response.Content.ReadAsStringAsync();
                    NoDiagnostics(html);
                    var document = await new HtmlParser().ParseDocumentAsync(html);
                    if (path.StartsWith("/game/", StringComparison.Ordinal))
                        Assert.StartsWith("Hype Score" + Math.Round(expected.Total!.Value) + "Stakes" + Math.Round(expected.Components.StakesScore)
                            + "Quality" + Math.Round(expected.Components.QualityScore) + "Closeness" + Math.Round(expected.Components.ClosenessScore),
                            document.QuerySelector("#detail-hype")!.TextContent);
                    else Assert.Equal("Hype" + Math.Round(expected.Total!.Value), document.QuerySelector($"#excitement-{game.Id}")!.TextContent);
                }
        }
        var resolved = await Resolve(game);
        Assert.Equal(expected.Total, resolved.Total);
        Assert.Equal(expected.Components.StakesScore, resolved.Components.StakesScore);
        Assert.Equal(expected.Components.QualityScore, resolved.Components.QualityScore);
        Assert.Equal(expected.Components.ClosenessScore, resolved.Components.ClosenessScore);
        Assert.Equal(expected.Components.HomeQualificationEvidenceKind, resolved.Components.HomeQualificationEvidenceKind);
        Assert.Equal(expected.Components.AwayQualificationEvidenceKind, resolved.Components.AwayQualificationEvidenceKind);
        Assert.Equal(expected.Components.HomeQualificationSource, resolved.Components.HomeQualificationSource);
        Assert.Equal(expected.Components.AwayQualificationSource, resolved.Components.AwayQualificationSource);
        Assert.Equal(expected.Components.HomePlayoffOdds, resolved.Components.HomePlayoffOdds);
        Assert.Equal(expected.Components.AwayPlayoffOdds, resolved.Components.AwayPlayoffOdds);
        Assert.Equal(expected.EvaluatedAt, resolved.EvaluatedAt);
    }

    [Theory]
    [InlineData("/game/acceptance-game")]
    [InlineData("/")]
    [InlineData("/nfl?week=4&season=2026&type=2")]
    public async Task Raced_page_identity_agrees_with_the_authoritative_matchup_supplying_hype(string path)
    {
        var game = await Seed("nfl");
        Start();
        await Capture();
        var original = await Resolve(game);
        Assert.Equal(53.6273891129, original.Total!.Value, 10);
        clock.SetUtcNow(new DateTimeOffset(game.ScheduledStart.AddHours(-2)));
        await Change(async db =>
        {
            var at = clock.GetUtcNow().UtcDateTime;
            var row = await db.Games.SingleAsync();
            var evidence = ProGameEvidence.Parse(row.ProEvidenceJson)!;
            evidence.ObservedAt = at;
            ProHypeContinuityInputs.RefreshLeverage(row, evidence);
            row.ProEvidenceJson = JsonSerializer.Serialize(evidence);
            foreach (var rating in await db.TeamRatings.ToArrayAsync()) rating.FetchedAt = at;
            (await db.PregameOdds.SingleAsync()).FetchedAt = at;
        });
        publication.Arm();
        var pending = client.GetStringAsync(path);
        await publication.Entered.Task.WaitAsync(HangDeadline);
        try
        {
            await Change(async db =>
            {
                var at = clock.GetUtcNow().UtcDateTime;
                var row = await db.Games.SingleAsync();
                row.HomeTeam = "Kansas City";
                row.HomeTeamAbbreviation = "KC";
                var evidence = ProGameEvidence.Parse(row.ProEvidenceJson)!;
                evidence.Home = "KC";
                evidence.ObservedAt = at;
                ProHypeContinuityInputs.RefreshLeverage(row, evidence);
                row.ProEvidenceJson = JsonSerializer.Serialize(evidence);
                var odds = await db.PregameOdds.SingleAsync();
                odds.HomeTeamAbbreviation = "KC";
                odds.FetchedAt = at;
                odds.HomeWinProbability = .7;
                odds.AwayWinProbability = .3;
                db.TeamRatings.Add(new TeamRating { Sport = "nfl", TeamAbbreviation = "KC", Season = 2026, SourceSeason = 2026,
                    Source = "powerrankingsguru", PowerRank = 28, FetchedAt = at });
                db.TeamRatings.Add(new TeamRating { Sport = "nfl", TeamAbbreviation = "KC", Season = 2026, SourceSeason = 2026,
                    Source = "kalshi", PlayoffProbability = .5, FetchedAt = at });
            });
        }
        finally { publication.Release(); }
        var html = await pending.WaitAsync(HangDeadline);
        NoDiagnostics(html);
        var document = await new HtmlParser().ParseDocumentAsync(html);
        var identity = path.StartsWith("/game/", StringComparison.Ordinal) ? document.Body!
            : document.QuerySelector($"a[href='/game/{game.Id}']")!;
        Assert.Contains(path.StartsWith("/game/", StringComparison.Ordinal) ? "Kansas City" : "KC", identity.TextContent);
        Assert.DoesNotContain("Philadelphia", identity.TextContent);
        Assert.DoesNotContain("PHI", identity.TextContent);
        var resolved = await Resolve(game);
        Assert.Equal(11.879196428571429, resolved.Total!.Value, 10);
        if (path.StartsWith("/game/", StringComparison.Ordinal))
        {
            Assert.Contains("KC", document.Title);
            Assert.StartsWith("Hype Score12Stakes1Quality25Closeness60", document.QuerySelector("#detail-hype")!.TextContent);
        }
        else Assert.Equal("Hype12", document.QuerySelector($"#excitement-{game.Id}")!.TextContent);
        Assert.Equal(document.QuerySelector(path.StartsWith("/game/", StringComparison.Ordinal) ? "#detail-hype" : $"#excitement-{game.Id}")!.TextContent,
            (await Page(path, true)).QuerySelector(path.StartsWith("/game/", StringComparison.Ordinal) ? "#detail-hype" : $"#excitement-{game.Id}")!.TextContent);
        Assert.Equal("{\"Total\":" + JsonSerializer.Serialize(resolved.Total) + "}", JsonSerializer.Serialize(resolved));
    }

    private async Task FailedCompetitionRefresh()
    {
        using var scope = app!.Services.CreateScope();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await scope.ServiceProvider.GetRequiredService<ProCompetitionEvidenceService>().RefreshAsync(deadline.Token);
    }

    private async Task RefreshInputs(Game game, bool competition = true)
    {
        await Change(async db =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            if (competition)
            {
                var row = await db.Games.SingleAsync(g => g.Id == game.Id);
                var evidence = ProGameEvidence.Parse(row.ProEvidenceJson)!;
                evidence.ObservedAt = now;
                evidence.CompletedGameIds = ["unrelated-result"];
                evidence.HomeProgress.Completed = evidence.AwayProgress.Completed = 4;
                ProHypeContinuityInputs.RefreshLeverage(row, evidence, .25);
                row.ProEvidenceJson = JsonSerializer.Serialize(evidence);
            }
            foreach (var rating in await db.TeamRatings.ToArrayAsync())
            {
                rating.FetchedAt = now;
                if (rating.PowerRank.HasValue) rating.PowerRank += 5;
                if (rating.RankPublishedAt.HasValue) rating.RankPublishedAt = now;
            }
            var odds = await db.PregameOdds.SingleAsync();
            odds.FetchedAt = now;
            odds.HomeWinProbability = .7;
            odds.AwayWinProbability = .3;
        });
    }

    [Theory]
    [InlineData("nfl", 54)]
    [InlineData("nba", 70)]
    [InlineData("nhl", 58)]
    [InlineData("mlb", 64)]
    public async Task Capture_before_visits_survives_prolonged_transport_gaps_first_HTTP_after_restart_and_complete_changing_recovery(string sport, int rounded)
    {
        var game = await Seed(sport);
        Start();
        var initial = await Current(game);
        Assert.True(initial.Components.IsComplete);
        using var scoring = app!.Services.CreateScope();
        var scorer = scoring.ServiceProvider.GetRequiredService<HypeScoreService>();
        var expected = new ProHypeResolution(scorer.CalculateFinalScore(game.Sport, initial.Components), initial.Components, ProHypeTests.Now);
        Assert.Equal(rounded, Math.Round(expected.Total!.Value));
        await Capture();
        if (sport == "nfl")
        {
            var captured = await Resolve(game);
            Assert.Equal(expected.Total, captured.Total);
            expected = captured;
        }
        await Change(db =>
        {
            db.Games.Add(new Game { Id = "unrelated-result", Sport = game.Sport, SeasonYear = game.SeasonYear, SeasonType = 2,
                HomeTeam = "Other", AwayTeam = "Other", HomeTeamAbbreviation = "A", AwayTeamAbbreviation = "B",
                ScheduledStart = ProHypeTests.Now.AddHours(-2), Status = "final" });
            return Task.CompletedTask;
        });
        var lag = await Current(game);
        Assert.False(lag.Components.StakesAvailable);
        Assert.True(lag.Components.QualityAvailable);
        Assert.True(lag.Components.ClosenessAvailable);
        Assert.True(double.IsNaN(scorer.CalculateFinalScore(game.Sport, lag.Components)));
        foreach (var elapsed in new[] { TimeSpan.FromHours(1), TimeSpan.FromDays(1), TimeSpan.FromHours(47) })
        {
            clock.Advance(elapsed);
            await FailedCompetitionRefresh();
            await Capture();
        }
        var expired = await Current(game);
        Assert.False(expired.Components.IsComplete);
        Assert.True(double.IsNaN(scorer.CalculateFinalScore(game.Sport, expired.Components)));
        Assert.Equal(initial.Assessment.Observations.Select(r => (r.Kind, r.Source, r.Team, r.ObservedAt, r.PublishedAt)),
            expired.Assessment.Observations.Select(r => (r.Kind, r.Source, r.Team, r.ObservedAt, r.PublishedAt)));
        await Restart();
        var firstHttp = await Page("/game/" + game.Id);
        Assert.StartsWith("Hype Score" + rounded + "Stakes" + Math.Round(expected.Components.StakesScore)
            + "Quality97Closeness100", firstHttp.QuerySelector("#detail-hype")!.TextContent);
        await WholePreview(game, expected);
        clock.Advance(TimeSpan.FromMinutes(2));
        await RefreshInputs(game);
        var complete = await Current(game);
        Assert.True(complete.Components.IsComplete);
        var replacement = new ProHypeResolution(scorer.CalculateFinalScore(game.Sport, complete.Components), complete.Components,
            clock.GetUtcNow().UtcDateTime);
        Assert.NotEqual(expected.Total, replacement.Total);
        Assert.NotEqual(expected.Components.StakesScore, replacement.Components.StakesScore);
        Assert.NotEqual(expected.Components.QualityScore, replacement.Components.QualityScore);
        Assert.Equal(60, replacement.Components.ClosenessScore, 8);
        await Capture();
        if (sport == "nfl")
        {
            var captured = await Resolve(game);
            Assert.Equal(replacement.Total, captured.Total);
            replacement = captured;
        }
        clock.Advance(TimeSpan.FromHours(1));
        await Restart();
        await WholePreview(game, replacement);
        foreach (var path in new[] { "/api/games", $"/api/games/{game.Id}/odds" })
        {
            var output = await client.GetStringAsync(path);
            NoDiagnostics(output);
            Assert.DoesNotContain("hype", output, StringComparison.OrdinalIgnoreCase);
        }
        await RefreshInputs(game, competition: false);
        string? recoveredContext = null;
        await Change(async db =>
        {
            var row = await db.Games.SingleAsync(g => g.Id == game.Id);
            recoveredContext = row.ProEvidenceJson;
            var evidence = ProGameEvidence.Parse(row.ProEvidenceJson)!;
            evidence.Rejection = ProCompetitionRejection.ConflictingSchedule;
            evidence.ObservedAt = clock.GetUtcNow().UtcDateTime;
            row.ProEvidenceJson = JsonSerializer.Serialize(evidence);
        });
        await Capture();
        var rejected = await Page("/game/" + game.Id, true);
        var text = rejected.QuerySelector("#detail-hype")!.TextContent;
        Assert.Contains("Hype unavailable", text);
        Assert.Contains("StakesUnavailable", text);
        Assert.Contains("Closeness60", text);
        Assert.DoesNotContain("QualityUnavailable", text);
        await Change(async db => (await db.Games.SingleAsync(g => g.Id == game.Id)).ProEvidenceJson = recoveredContext);
        clock.Advance(TimeSpan.FromHours(2));
        await Restart();
        Assert.Contains("Hype unavailable", (await Page("/game/" + game.Id)).QuerySelector("#detail-hype")!.TextContent);
        Assert.Null((await Resolve(game)).Total);
        Assert.Equal(3, outbound.Failures);
        Assert.Equal(0, outbound.Unexpected);
    }

    [Theory]
    [InlineData("nfl", 1, 0)]
    [InlineData("nba", 1, 0)]
    [InlineData("nhl", 1, 0)]
    [InlineData("mlb", 1, 0)]
    [InlineData("nfl", 3, 100)]
    [InlineData("nba", 3, 100)]
    [InlineData("nhl", 3, 100)]
    [InlineData("mlb", 3, 100)]
    [InlineData("nba", 5, 75)]
    public async Task Phase_specific_complete_previews_are_captured_before_visits_and_keep_genuine_zero_stakes(string sport, int phase, double stakes)
    {
        var game = await Seed(sport, phase);
        Start();
        var current = await Current(game);
        Assert.True(current.Components.IsComplete);
        Assert.Equal(stakes, current.Components.StakesScore);
        using var scope = app!.Services.CreateScope();
        var scorer = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
        var expected = new ProHypeResolution(scorer.CalculateFinalScore(sport, current.Components), current.Components, ProHypeTests.Now);
        Assert.True(expected.Total > 0);
        await Capture();
        clock.Advance(TimeSpan.FromDays(3));
        Assert.False((await Current(game)).Components.StakesAvailable);
        await Restart();
        if (phase == 1)
        {
            using var response = await client.GetAsync("/game/" + game.Id);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
            foreach (var path in new[] { "/", LeaguePath(game) })
                Assert.Null((await Page(path)).QuerySelector($"#excitement-{game.Id}"));
            var retained = await Resolve(game);
            Assert.Equal(expected.Total, retained.Total);
            Assert.Equal(0, retained.Components.StakesScore);
            Assert.Equal(expected.Components.QualityScore, retained.Components.QualityScore);
            Assert.Equal(expected.Components.ClosenessScore, retained.Components.ClosenessScore);
            Assert.Equal(ProHypeTests.Now, retained.EvaluatedAt);
        }
        else await WholePreview(game, expected);
        Assert.Equal(0, outbound.Unexpected);
        Assert.Equal(0, outbound.Failures);
    }

    [Fact]
    public async Task Never_complete_capture_keeps_independent_current_metrics_and_public_numeric_zero_is_not_unavailable()
    {
        var game = await Seed("nba");
        await Change(async db => (await db.TeamRatings.FirstAsync(r => r.Source == "kalshi")).PlayoffProbability = null);
        Start();
        await Capture();
        clock.Advance(TimeSpan.FromMinutes(1));
        await Restart();
        var detail = (await Page("/game/" + game.Id)).QuerySelector("#detail-hype")!;
        Assert.Contains("Hype unavailable", detail.TextContent);
        Assert.Contains("StakesUnavailableQuality97Closeness100", detail.TextContent);
        var absent = await Resolve(game);
        Assert.Null(absent.Total);
        Assert.False(absent.Components.StakesAvailable);
        Assert.True(absent.Components.QualityAvailable);
        Assert.True(absent.Components.ClosenessAvailable);
        using (var scope = app!.Services.CreateScope())
        {
            var scorer = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
            foreach (var sport in new[] { "nfl", "nba", "nhl", "mlb" })
            {
                var numeric = new HypeComponents { StakesScore = 0, QualityScore = 0, ClosenessScore = 0 };
                Assert.Equal(0, scorer.CalculateFinalScore(sport, numeric));
                numeric.StakesAvailable = false;
                Assert.True(double.IsNaN(scorer.CalculateFinalScore(sport, numeric)));
            }
        }
        clock.Advance(TimeSpan.FromDays(3));
        await Restart();
        foreach (var path in new[] { "/", LeaguePath(game), "/game/" + game.Id })
            Assert.Contains("Hype unavailable", (await Page(path)).Body!.TextContent);
        Assert.Null((await Resolve(game)).Total);
        Assert.Equal(0, outbound.Unexpected);
        Assert.Equal(0, outbound.Failures);
    }

    [Fact]
    public async Task First_HTTP_after_restart_and_update_reload_agree_one_tick_before_and_exactly_at_scheduled_kickoff()
    {
        var game = await Seed("nfl");
        Start();
        await Capture();
        clock.SetUtcNow(new DateTimeOffset(game.ScheduledStart.AddTicks(-1)));
        await Restart();
        Assert.StartsWith("Hype Score54", (await Page("/game/" + game.Id)).QuerySelector("#detail-hype")!.TextContent);
        clock.Advance(TimeSpan.FromTicks(1));
        foreach (var update in new[] { false, true })
            foreach (var path in new[] { "/game/" + game.Id, "/", LeaguePath(game) })
                Assert.Contains("Hype unavailable", (await Page(path, update)).Body!.TextContent);
        await Restart();
        Assert.Contains("Hype unavailable", (await Page("/game/" + game.Id)).QuerySelector("#detail-hype")!.TextContent);
        Assert.Null((await Resolve(game)).Total);
        Assert.Equal(0, outbound.Unexpected);
        Assert.Equal(0, outbound.Failures);
    }

    [Fact]
    public async Task A_delayed_registered_capture_cannot_resurrect_a_context_invalidated_by_an_HTTP_request()
    {
        var game = await Seed("nfl");
        Start();
        await Capture();
        publication.Arm();
        var delayedCapture = Capture();
        await publication.Entered.Task.WaitAsync(HangDeadline);
        try
        {
            await Change(async db => (await db.Games.SingleAsync()).Status = "postponed");
            var invalidated = await Page("/game/" + game.Id);
            Assert.Null(invalidated.QuerySelector("#detail-hype"));
            publication.Release();
            await delayedCapture.WaitAsync(HangDeadline);
            await Change(async db => (await db.Games.SingleAsync()).Status = "scheduled");
            clock.Advance(TimeSpan.FromDays(3));
            await Restart();
            Assert.Contains("Hype unavailable", (await Page("/game/" + game.Id)).QuerySelector("#detail-hype")!.TextContent);
            Assert.Null((await Resolve(game)).Total);
            Assert.Equal(0, outbound.Unexpected);
            Assert.Equal(0, outbound.Failures);
        }
        finally
        {
            publication.Release();
            await delayedCapture;
        }
    }

    [Theory]
    [InlineData("qualification-credit")]
    [InlineData("duplicate-ranking")]
    public async Task Corrupt_saved_provenance_cannot_claim_support_that_did_not_produce_the_whole_preview(string corruption)
    {
        var game = await Seed();
        Start();
        await Capture();
        await Change(async db =>
        {
            var state = await db.ProHypeSnapshots.SingleAsync();
            var json = JsonNode.Parse(state.SnapshotJson!)!;
            if (corruption == "qualification-credit")
            {
                json["Components"]!["AwayQualificationSource"] = "moneypuck_qualification";
                json["Components"]!["AwayQualificationEvidenceKind"] = (int)ProQualificationEvidenceKind.MoneyPuckModel;
            }
            else
            {
                var support = json["SupportingEvidence"]!.AsArray();
                var ranking = support.Select((node, index) => (node, index)).Where(x => x.node!["Kind"]!.GetValue<string>() == "ranking").ToArray();
                support[ranking[1].index] = ranking[0].node!.DeepClone();
            }
            state.SnapshotJson = json.ToJsonString();
        });
        clock.Advance(TimeSpan.FromDays(3));
        await Restart();
        var detail = await Page("/game/" + game.Id);
        Assert.Contains("Hype unavailable", detail.QuerySelector("#detail-hype")!.TextContent);
        Assert.DoesNotContain("Stakes and hype use", detail.QuerySelector("#detail-hype")!.TextContent);
        Assert.Null((await Resolve(game)).Total);
        Assert.Equal(0, outbound.Unexpected);
        Assert.Equal(0, outbound.Failures);
    }

    [Fact]
    public async Task First_visit_after_recreation_credits_model_supported_hype_when_only_evidence_establishes_regular_phase()
    {
        await using var harness = new MoneyPuckQualificationHarness();
        await harness.StartAsync(configureServices: services =>
            services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(() => harness.Handler)));
        using (var scope = harness.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "035_ProHypeLifecycle.sql")));
        }
        Assert.Equal(32, await harness.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        var game = ProHypeTests.Game();
        game.Id = "phase-from-evidence";
        game.HomeTeamAbbreviation = "OTT";
        game.AwayTeamAbbreviation = "NSH";
        game.ScheduledStart = harness.Clock.Now.AddDays(3).AddHours(4).UtcDateTime;
        var evidence = ProHypeTests.Evidence(game);
        evidence.ObservedAt = harness.Clock.Now.UtcDateTime;
        game.ProEvidenceJson = JsonSerializer.Serialize(evidence);
        game.SeasonType = null;
        var odds = ProHypeTests.Odds(game);
        odds.FetchedAt = harness.Clock.Now.UtcDateTime;
        using (var scope = harness.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            db.Games.Add(game);
            db.PregameOdds.Add(odds);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().CaptureAsync();
        }
        var requests = harness.Handler.Requests.Count;
        harness.Clock.Now = harness.Clock.Now.AddDays(3);
        harness.Client.Dispose();
        await harness.App.DisposeAsync();
        await using var peer = harness.CreatePeerApplication();
        await using var recreated = peer.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => harness.Handler))));
        using var viewer = recreated.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        foreach (var path in new[] { "/game/" + game.Id, "/", $"/nhl?date={game.ScheduledStart:yyyy-MM-dd}" })
        {
            var html = await viewer.GetStringAsync(path);
            NoDiagnostics(html);
            var document = await new HtmlParser().ParseDocumentAsync(html);
            var preview = document.QuerySelector(path.StartsWith("/game/", StringComparison.Ordinal)
                ? "#detail-hype" : $"#hype-credit-{game.Id}")!;
            Assert.NotNull(preview);
            Assert.Contains("Stakes and hype use model estimates for NSH / OTT from MoneyPuck.", preview.TextContent);
            Assert.Equal("https://moneypuck.com/predictions.htm", preview.QuerySelector("a")!.GetAttribute("href"));
            if (path.StartsWith("/game/", StringComparison.Ordinal))
                Assert.StartsWith("Hype Score58Stakes6Quality97Closeness100", preview.TextContent);
            else Assert.Equal("Hype58", document.QuerySelector($"#excitement-{game.Id}")!.TextContent);
        }
        using var verification = recreated.Services.CreateScope();
        var retained = await verification.ServiceProvider.GetRequiredService<IProHypeLifecycle>().ResolveAsync(game.Id);
        Assert.Equal(ProQualificationEvidenceKind.MoneyPuckModel, retained.Components.AwayQualificationEvidenceKind);
        Assert.Equal(ProQualificationEvidenceKind.MoneyPuckModel, retained.Components.HomeQualificationEvidenceKind);
        Assert.Equal(.2518, retained.Components.AwayPlayoffOdds);
        Assert.Equal(.6804, retained.Components.HomePlayoffOdds);
        harness.Clock.Now = harness.Clock.Now.AddMinutes(1);
        using (var write = recreated.Services.CreateScope())
        {
            var db = write.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var veto = new PlayoffQuoteEvidence { Version = 1, Provider = "kalshi", ValidationPolicy = "kalshi-qualification-v1",
                Sport = "nhl", Team = "NSH", Season = 2027, EventId = "KXNHLPLAYOFF-27", MarketId = "KXNHLPLAYOFF-27-NSH",
                ContractVerified = true, State = "resolution_blocked", ResolutionBlocked = true, ReasonCode = "rules_changed",
                ObservedAt = harness.Clock.Now.UtcDateTime };
            var row = new TeamRating { Sport = "nhl", TeamAbbreviation = "NSH", Season = 2027, SourceSeason = 2027,
                Source = "kalshi", FetchedAt = harness.Clock.Now.UtcDateTime, PlayoffEvidenceJson = JsonSerializer.Serialize(veto) };
            db.TeamRatings.Add(row);
            await db.SaveChangesAsync();
            db.TeamRatings.Remove(row);
            await db.SaveChangesAsync();
            await write.ServiceProvider.GetRequiredService<IProHypeLifecycle>().CaptureAsync();
        }
        await recreated.DisposeAsync();
        await using var revokedApp = peer.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => harness.Handler))));
        using var nextViewer = revokedApp.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var rejectedHtml = await nextViewer.GetStringAsync("/game/" + game.Id);
        NoDiagnostics(rejectedHtml);
        var rejected = (await new HtmlParser().ParseDocumentAsync(rejectedHtml)).QuerySelector("#detail-hype")!;
        Assert.Contains("Hype unavailable", rejected.TextContent);
        Assert.DoesNotContain("Stakes and hype use", rejected.TextContent);
        Assert.Equal(requests, harness.Handler.Requests.Count);
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (app is not null) await app.DisposeAsync();
        await entry.DisposeAsync();
        await database.DisposeAsync();
    }
}
