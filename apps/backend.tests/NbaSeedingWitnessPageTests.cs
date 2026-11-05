using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AngleSharp.Html.Parser;
using backend.Data;
using backend.Models;
using backend.Services;
using backend.Services.Espn;
using backend.Services.Kalshi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.tests;

public sealed class NbaSeedingWitnessPageTests
{
    private sealed class Handler(NbaSeedingScenario scenario, string mode) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        private JsonElement Event(NbaSeedingScenario.Contest contest) => mode == "cup" && contest.Id == scenario.Target.Id
            ? scenario.Change(contest, e => e["competitions"]![0]!["notes"] = JsonSerializer.SerializeToNode(new[] { new { headline = "NBA Cup - Group Play" } }))
            : scenario.EarlyEvent(contest);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri.AbsoluteUri);
            string body;
            if (uri.AbsolutePath.EndsWith("/basketball/nba/powerindex", StringComparison.Ordinal))
            {
                var rest = new Queue<int>(Enumerable.Range(1, 30).Except([4, 9]));
                body = JsonSerializer.Serialize(new
                {
                    pagination = new { pages = 1 }, requestedSeason = new { year = 2027 }, currentValues = new { season = 2027 },
                    categories = new[] { new { name = "bpi", names = new[] { "bpi", "bpirank" } } },
                    teams = scenario.Teams.Select(team => new
                    {
                        team = new { id = scenario.TeamId(team), abbreviation = team, displayName = "Synthetic " + team },
                        categories = new[] { new { name = "bpi", values = new[] { 0, team == "BOS" ? 4 : team == "MIA" ? 9 : rest.Dequeue() } } }
                    }).ToArray()
                });
            }
            else if (uri.AbsolutePath == "/trade-api/v2/events/KXNBAPLAYOFF-27")
            {
                var value = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NbaSeeding", "qualification.json")))!;
                foreach (var market in value["event"]!["markets"]!.AsArray())
                {
                    var rejected = mode == "qualification" && market!["ticker"]!.GetValue<string>().EndsWith("-BOS", StringComparison.Ordinal);
                    market!["yes_bid_dollars"] = rejected ? "0.0000" : "0.8800";
                    market["yes_ask_dollars"] = rejected ? "0.0500" : "0.9200";
                    market["yes_bid_size_fp"] = rejected ? "0.00" : "10.00";
                }
                body = value.ToJsonString();
            }
            else if (uri.AbsolutePath.EndsWith("/basketball/nba/teams", StringComparison.Ordinal))
                body = JsonSerializer.Serialize(new { sports = new[] { new { leagues = new[] { new {
                    season = new { year = 2027 }, teams = scenario.Teams.Select(team => new { team = new { id = scenario.TeamId(team), abbreviation = team } }).ToArray()
                } } } } });
            else if (uri.AbsolutePath.Contains("/basketball/nba/teams/", StringComparison.Ordinal) && uri.AbsolutePath.EndsWith("/schedule", StringComparison.Ordinal))
            {
                var id = uri.Segments[^2].Trim('/');
                var team = scenario.Teams.Single(team => scenario.TeamId(team) == id);
                body = scenario.ScheduleJson(team, Event).GetRawText();
            }
            else if (uri.AbsolutePath.EndsWith("/basketball/nba/scoreboard", StringComparison.Ordinal))
                body = JsonSerializer.Serialize(new { events = new[] { Event(scenario.Target) } });
            else throw new InvalidOperationException("Unexpected offline request: " + uri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("qualification")]
    [InlineData("cup")]
    public async Task Registered_provisional_collection_captures_only_fully_supported_hype_and_preserves_the_original_tuple(string mode)
    {
        var scenario = new NbaSeedingScenario();
        using var handler = new Handler(scenario, mode);
        await using var h = new MoneyPuckQualificationHarness();
        h.Clock.Now = new DateTimeOffset(NbaSeedingScenario.Now);
        await h.StartAsync(configureServices: services =>
        {
            services.AddHttpClient<ProCompetitionEvidenceService>().ConfigurePrimaryHttpMessageHandler(() => handler);
            services.AddHttpClient<EspnPowerIndexService>().ConfigurePrimaryHttpMessageHandler(() => handler);
            services.AddHttpClient<KalshiPlayoffRatingsService>().ConfigurePrimaryHttpMessageHandler(() => handler);
        });
        var game = scenario.Game;
        using (var scope = h.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "035_ProHypeLifecycle.sql")));
            db.Games.Add(game);
            var odds = ProHypeTests.Odds(game, .6);
            odds.FetchedAt = h.Clock.Now.UtcDateTime;
            db.PregameOdds.Add(odds);
            db.MoneyPuckRefreshOperations.Add(new() { NextAttemptAtTicks = h.Clock.Now.AddDays(3).Ticks, DeniedUntilTicks = h.Clock.Now.AddDays(4).Ticks, Denied = true });
            db.NhlGuruRefreshOperations.Add(new() { NextAttemptAtTicks = h.Clock.Now.AddDays(2).Ticks });
            await db.SaveChangesAsync();
            Assert.Equal(30, await scope.ServiceProvider.GetRequiredService<EspnPowerIndexService>().RefreshNbaRatingsAsync(null));
            Assert.Equal(mode == "qualification" ? 29 : 30, await scope.ServiceProvider.GetRequiredService<KalshiPlayoffRatingsService>().RefreshAsync("nba", 2027, default));
            await scope.ServiceProvider.GetRequiredService<ProCompetitionEvidenceService>().RefreshAsync(default);
            var stored = await db.Games.AsNoTracking().SingleAsync();
            var evidence = ProGameEvidence.Parse(stored.ProEvidenceJson)!;
            Assert.True(evidence.HomeProgress.ScheduleVerified && evidence.AwayProgress.ScheduleVerified);
            Assert.Equal((7, 8), (evidence.HomeProgress.Completed, evidence.AwayProgress.Completed));
            Assert.Equal((82, 82), (evidence.HomeProgress.Scheduled, evidence.AwayProgress.Scheduled));
            Assert.Equal(mode == "cup" ? null : true, evidence.HomeProgress.SeedingUnresolved);
            Assert.Equal(mode == "cup" ? null : true, evidence.AwayProgress.SeedingUnresolved);
            Assert.Equal(NbaSeedingScenario.Now, evidence.ObservedAt);
            var raw = await scope.ServiceProvider.GetRequiredService<HypeScoreService>().GetHypeComponentsAsync(stored, null);
            Assert.Equal(mode != "qualification", raw.IsComplete);
            await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().CaptureAsync();
            Assert.Equal(mode != "qualification", (await db.ProHypeSnapshots.AsNoTracking().SingleAsync()).SnapshotJson is not null);
            var mp = await db.MoneyPuckRefreshOperations.AsNoTracking().SingleAsync();
            Assert.True(mp.Denied); Assert.False(mp.Active);
            Assert.Equal(h.Clock.Now.AddDays(3).Ticks, mp.NextAttemptAtTicks);
            Assert.Equal(h.Clock.Now.AddDays(4).Ticks, mp.DeniedUntilTicks);
            Assert.Equal(h.Clock.Now.AddDays(2).Ticks, (await db.NhlGuruRefreshOperations.AsNoTracking().SingleAsync()).NextAttemptAtTicks);
        }
        var original = await Resolve(h, game);
        var available = mode != "qualification";
        var requests = handler.Requests.Count;
        if (available)
        {
            Assert.Equal(55.750850127514205, original.Total!.Value, 10);
            Assert.Equal(0.32690783245476907, original.Components.StakesScore, 12);
            Assert.Equal(79.13194444444444, original.Components.QualityScore, 10);
            Assert.Equal(80, original.Components.ClosenessScore, 10);
            Assert.Equal("espn_bpi", original.Components.QualitySource);
        }
        else
        {
            Assert.Null(original.Total);
            Assert.False(original.Components.StakesAvailable);
            Assert.True(original.Components.QualityAvailable && original.Components.ClosenessAvailable);
            if (mode == "qualification") Assert.Null(original.Components.HomePlayoffOdds);
        }
        await VerifyPages(h, game, available);
        Assert.Equal(requests, handler.Requests.Count);
        Assert.Empty(h.Handler.Requests);
        if (!available) return;
        h.Clock.Now = h.Clock.Now.AddHours(2);
        await h.RestartApplicationAsync();
        var retained = await Resolve(h, game);
        Assert.Equal(original.Total, retained.Total);
        Assert.Equal(original.EvaluatedAt, retained.EvaluatedAt);
        Assert.Equal(JsonSerializer.Serialize(original.Components), JsonSerializer.Serialize(retained.Components));
        await VerifyPages(h, game, true);
        Assert.Equal(requests, handler.Requests.Count);
        Assert.Empty(h.Handler.Requests);
        h.Clock.Now = new DateTimeOffset(game.ScheduledStart);
        Assert.Null((await Resolve(h, game)).Total);
    }

    private static async Task<ProHypeResolution> Resolve(MoneyPuckQualificationHarness h, Game game)
    {
        using var scope = h.App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().ResolveAsync(game.Id);
    }

    private static async Task VerifyPages(MoneyPuckQualificationHarness h, Game game, bool available)
    {
        foreach (var path in new[] { "/", "/nba?date=2026-11-16", "/game/" + game.Id })
        {
            var html = await h.Client.GetStringAsync(path);
            using var document = await new HtmlParser().ParseDocumentAsync(html, default);
            var metric = document.QuerySelector(path.StartsWith("/game/", StringComparison.Ordinal) ? "#detail-hype" : "#excitement-" + game.Id);
            Assert.NotNull(metric);
            if (available)
            {
                Assert.Matches(@"(?<!\d)56(?!\d)", metric.TextContent);
                Assert.DoesNotContain("unavailable", metric.TextContent, StringComparison.OrdinalIgnoreCase);
            }
            else Assert.Contains("unavailable", metric.TextContent, StringComparison.OrdinalIgnoreCase);
            ProHypeOutputAssertions.NoDiagnostics(html);
        }
    }
}
