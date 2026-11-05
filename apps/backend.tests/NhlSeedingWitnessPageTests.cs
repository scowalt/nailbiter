using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using backend.Data;
using backend.Models;
using backend.Services;
using backend.Services.Kalshi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.tests;

public sealed class NhlSeedingWitnessPageTests
{
    private sealed class Handler(NhlSeedingScenario scenario) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public string? RejectedQualificationTeam { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri.AbsoluteUri);
            string body;
            if (uri.AbsoluteUri == NhlPowerRankingsGuruService.RankingsUrl)
                body = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NhlGuru", "rankings.html"));
            else if (uri.AbsolutePath == "/trade-api/v2/events/KXNHLPLAYOFF-27")
            {
                var value = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NhlSeeding", "qualification.json")))!;
                foreach (var market in value["event"]!["markets"]!.AsArray())
                {
                    var rejected = market!["ticker"]!.GetValue<string>().EndsWith("-" + RejectedQualificationTeam, StringComparison.Ordinal);
                    market["yes_bid_dollars"] = rejected ? "0.0000" : "0.8800";
                    market["yes_ask_dollars"] = rejected ? "0.0500" : "0.9200";
                    market["yes_bid_size_fp"] = rejected ? "0.00" : "10.00";
                }
                body = value.ToJsonString();
            }
            else if (uri.AbsolutePath.EndsWith("/hockey/nhl/teams", StringComparison.Ordinal))
                body = JsonSerializer.Serialize(new { sports = new[] { new { leagues = new[] { new {
                    season = new { year = 2027 }, teams = scenario.Teams.Select(team => new { team = new { id = scenario.TeamId(team), abbreviation = team } }).ToArray()
                } } } } });
            else if (uri.AbsolutePath.Contains("/hockey/nhl/teams/", StringComparison.Ordinal) && uri.AbsolutePath.EndsWith("/schedule", StringComparison.Ordinal))
            {
                var id = uri.Segments[^2].Trim('/');
                var team = scenario.Teams.Single(team => scenario.TeamId(team) == id);
                body = scenario.ScheduleJson(team).GetRawText();
            }
            else if (uri.AbsolutePath.EndsWith("/hockey/nhl/scoreboard", StringComparison.Ordinal))
                body = JsonSerializer.Serialize(new { events = new[] { scenario.Event(scenario.Target) } });
            else throw new InvalidOperationException("Unexpected offline request: " + uri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registered_collection_keeps_exact_scoring_and_qualification_eligibility_independent(bool rejectedQualification)
    {
        var scenario = new NhlSeedingScenario();
        using var handler = new Handler(scenario) { RejectedQualificationTeam = rejectedQualification ? "ANA" : null };
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync(configureServices: services =>
        {
            services.AddHttpClient<ProCompetitionEvidenceService>().ConfigurePrimaryHttpMessageHandler(() => handler);
            services.AddHttpClient<NhlPowerRankingsGuruService>().ConfigurePrimaryHttpMessageHandler(() => handler);
            services.AddHttpClient<KalshiPlayoffRatingsService>().ConfigurePrimaryHttpMessageHandler(() => handler);
        });
        var game = scenario.Game;
        using (var scope = h.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "035_ProHypeLifecycle.sql")));
            db.Games.Add(game);
            var odds = ProHypeTests.Odds(game);
            odds.FetchedAt = h.Clock.Now.UtcDateTime;
            db.PregameOdds.Add(odds);
            db.MoneyPuckRefreshOperations.Add(new() { NextAttemptAtTicks = h.Clock.Now.AddDays(3).Ticks, DeniedUntilTicks = h.Clock.Now.AddDays(4).Ticks, Denied = true });
            await db.SaveChangesAsync();
            Assert.Equal(32, await scope.ServiceProvider.GetRequiredService<NhlPowerRankingsGuruService>().RefreshAsync());
            Assert.Equal(rejectedQualification ? 31 : 32, await scope.ServiceProvider.GetRequiredService<KalshiPlayoffRatingsService>().RefreshAsync("nhl", 2027, default));
            await scope.ServiceProvider.GetRequiredService<ProCompetitionEvidenceService>().RefreshAsync(default);
            var stored = await db.Games.AsNoTracking().SingleAsync();
            var evidence = ProGameEvidence.Parse(stored.ProEvidenceJson)!;
            Assert.True(evidence.HomeProgress.SeedingUnresolved);
            Assert.True(evidence.AwayProgress.SeedingUnresolved);
            Assert.Equal(scenario.Game.ScheduledStart, evidence.ScheduledStart);
            Assert.Equal(NhlSeedingScenario.Now, evidence.ObservedAt);
            Assert.Contains("constructive", evidence.HomeProgress.Reason);
            await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().CaptureAsync();
            Assert.Equal(!rejectedQualification, (await db.ProHypeSnapshots.AsNoTracking().SingleAsync()).SnapshotJson is not null);
            var mp = await db.MoneyPuckRefreshOperations.AsNoTracking().SingleAsync();
            Assert.True(mp.Denied);
            Assert.False(mp.Active);
            Assert.Equal(h.Clock.Now.AddDays(3).Ticks, mp.NextAttemptAtTicks);
            Assert.Equal(h.Clock.Now.AddDays(4).Ticks, mp.DeniedUntilTicks);
        }
        var original = await Resolve(h, game);
        if (rejectedQualification)
        {
            Assert.Null(original.Total);
            Assert.False(original.Components.StakesAvailable);
            Assert.True(original.Components.QualityAvailable && original.Components.ClosenessAvailable);
            Assert.Null(original.Components.HomePlayoffOdds);
            foreach (var path in new[] { "/", "/nhl?date=2026-10-11", "/game/" + game.Id })
            {
                var html = await h.Client.GetStringAsync(path);
                using var document = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(html, default);
                var metric = document.QuerySelector(path.StartsWith("/game/", StringComparison.Ordinal) ? "#detail-hype" : "#excitement-" + game.Id);
                Assert.NotNull(metric);
                Assert.Contains("unavailable", metric.TextContent, StringComparison.OrdinalIgnoreCase);
                ProHypeOutputAssertions.NoDiagnostics(html);
            }
            Assert.Empty(h.Handler.Requests);
            return;
        }
        Assert.Equal(54.038261599633465, original.Total!.Value, 10);
        Assert.Equal(0.03048147007736855, original.Components.StakesScore, 12);
        Assert.Equal(68.6502659574468, original.Components.QualityScore, 10);
        Assert.Equal(100, original.Components.ClosenessScore);
        Assert.Equal(NhlPowerRankingsGuruService.Source, original.Components.QualitySource);
        var requests = handler.Requests.Count;
        await VerifyPages(h, game);
        Assert.Equal(requests, handler.Requests.Count);
        h.Clock.Now = h.Clock.Now.AddHours(2);
        await h.RestartApplicationAsync();
        var retained = await Resolve(h, game);
        Assert.Equal(original.Total, retained.Total);
        Assert.Equal(original.EvaluatedAt, retained.EvaluatedAt);
        Assert.Equal(JsonSerializer.Serialize(original.Components), JsonSerializer.Serialize(retained.Components));
        await VerifyPages(h, game);
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

    private static async Task VerifyPages(MoneyPuckQualificationHarness h, Game game)
    {
        foreach (var path in new[] { "/", "/nhl?date=2026-10-11", "/game/" + game.Id })
        {
            var html = await h.Client.GetStringAsync(path);
            using var document = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(html, default);
            var metric = document.QuerySelector(path.StartsWith("/game/", StringComparison.Ordinal) ? "#detail-hype" : "#excitement-" + game.Id);
            Assert.NotNull(metric);
            Assert.Matches(@"(?<!\d)54(?!\d)", metric.TextContent);
            Assert.Contains("Quality and hype use", html);
            Assert.Contains("href=\"https://powerrankingsguru.com/nhl/power-rankings\"", html);
            Assert.DoesNotContain("Hype unavailable", html);
            ProHypeOutputAssertions.NoDiagnostics(html);
        }
    }
}
