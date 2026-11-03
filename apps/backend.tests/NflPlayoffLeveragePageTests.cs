using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AngleSharp.Html.Parser;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace backend.tests;

public sealed partial class NflPlayoffPageTests
{
    private sealed class NflScheduleWire(Func<HttpRequestMessage, string> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body(request)) });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NflPlayoffLeverage_synthetic_forecasts_flow_through_registered_collector_persistence_public_scoring_and_all_pages(bool decimalScores)
    {
        clock.Now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var game = NflPlayoffLeverageTests.Game(); game.Id = "control-7-DET-GB";
        var source = new NflPlayoffLeverageTests.Handler { ServerDate = clock.Now.UtcDateTime };
        var schedules = NflPlayoffLeverageTests.Schedules(score => decimalScores ? score + ".0" : score);
        var boardEvent = JsonNode.Parse(schedules.Single(s => s.Team == "DET").Events.Single(e => e.GetProperty("id").GetString() == game.Id).GetRawText())!;
        boardEvent["season"]!["type"] = 2;
        var espn = new NflScheduleWire(request =>
        {
            var url = request.RequestUri!;
            if (url.AbsolutePath.EndsWith("/teams"))
                return JsonSerializer.Serialize(new { sports = new[] { new { leagues = new[] { new { season = new { year = 2026 }, teams = schedules.Select(s => new { team = new { id = s.Team, abbreviation = s.Team } }) } } } } });
            if (url.AbsolutePath.EndsWith("/schedule"))
            {
                var team = url.Segments[^2].TrimEnd('/');
                var schedule = schedules.Single(s => s.Team == team);
                return JsonSerializer.Serialize(new { status = "success", timestamp = clock.Now.UtcDateTime, season = new { year = 2026, type = 2 }, team = new { abbreviation = team, groups = new { id = "controlled-division" } }, events = schedule.Events });
            }
            Assert.EndsWith("/scoreboard", url.AbsolutePath);
            return JsonSerializer.Serialize(new { events = new[] { boardEvent } });
        });
        using var wired = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddHttpClient<NflPlayoffLeverageCollector>().ConfigurePrimaryHttpMessageHandler(() => source);
            services.RemoveAll<ProCompetitionEvidenceService>();
            services.AddScoped(sp => new ProCompetitionEvidenceService(new HttpClient(espn, disposeHandler: false),
                sp.GetRequiredService<ExcitementDbContext>(), clock, NullLogger<ProCompetitionEvidenceService>.Instance,
                sp.GetRequiredService<NflPlayoffLeverageCollector>()));
        }));
        using var browser = wired.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using (var scope = wired.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            db.Games.Add(game);
            var ranks = NflPlayoffLeverageTests.Ranks(game);
            foreach (var row in ranks) row.FetchedAt = clock.Now.UtcDateTime;
            db.TeamRatings.AddRange(ranks);
            foreach (var team in new[] { "DET", "GB" }) db.TeamRatings.Add(new TeamRating
            {
                Sport = "nfl", TeamAbbreviation = team, Source = "kalshi", SourceSeason = 2026, Season = 2026,
                PlayoffProbability = .62, FetchedAt = clock.Now.UtcDateTime
            });
            var odds = NflPlayoffLeverageTests.Odds(game); odds.FetchedAt = clock.Now.UtcDateTime;
            db.PregameOdds.Add(odds);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<ProCompetitionEvidenceService>().RefreshAsync(default);
            var evidence = ProGameEvidence.Parse(game.ProEvidenceJson)!;
            Assert.True(evidence.VerifiedUnstarted, evidence.Reason);
            Assert.True(evidence.NflPlayoffLeverage!.ScheduleVerified, evidence.NflPlayoffLeverage.Reason);
            Assert.Equal(6, evidence.HomeProgress.Completed); Assert.Equal(6, evidence.AwayProgress.Completed);
            var hype = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
            var components = await hype.GetHypeComponentsAsync(game, null);
            Assert.True(components.IsComplete, components.StakesExplanation);
            Assert.Equal(20, components.StakesScore, 8);
            Assert.Equal(59, Math.Round(hype.CalculateFinalScore("nfl", components)));
            Assert.Equal(.62, components.HomePlayoffOdds); Assert.Equal(.62, components.AwayPlayoffOdds);
            var direct = hype.CalculateFromEvidence(game, await db.TeamRatings.Where(r => r.Sport == "nfl").ToListAsync(), odds, [], clock.Now.UtcDateTime);
            Assert.Equal(components.StakesScore, direct.StakesScore);
            Assert.Equal(components.QualityScore, direct.QualityScore);
            Assert.Equal(components.ClosenessScore, direct.ClosenessScore);
        }
        Assert.Equal(3, source.Requests.Count);
        foreach (var path in new[] { "/", "/nfl?week=7&season=2026&type=2", "/game/" + game.Id })
        {
            var rendered = await browser.GetStringAsync(path);
            if (path.StartsWith("/game/") && Environment.GetEnvironmentVariable("NFL_LEVERAGE_ARTIFACT_DIR") is string artifactDirectory)
            {
                Directory.CreateDirectory(artifactDirectory);
                await File.WriteAllTextAsync(Path.Combine(artifactDirectory, "nfl-leverage.html"), rendered);
            }
            using var doc = await new HtmlParser().ParseDocumentAsync(rendered);
            if (path.StartsWith("/game/"))
            {
                Assert.Contains("Stakes20", string.Concat(doc.QuerySelector("#detail-hype")!.TextContent.Where(c => !char.IsWhiteSpace(c))));
                var forecast = doc.QuerySelector("#nfl-playoff-leverage")!;
                Assert.Contains("GB", forecast.TextContent); Assert.Contains("DET", forecast.TextContent);
                Assert.Contains("82%", forecast.TextContent); Assert.Contains("57%", forecast.TextContent);
                Assert.Contains("74%", forecast.TextContent); Assert.Contains("54%", forecast.TextContent);
                Assert.Contains("Model estimates from", forecast.TextContent);
                Assert.Contains("PlayoffStatus", forecast.TextContent);
                var outlook = doc.QuerySelector("#detail-comparison")!.Children.Single(row =>
                    row.Children.Length == 3 && row.Children[1].TextContent == "Playoff outlook");
                Assert.Matches(@"^62\s*%$", outlook.Children[0].TextContent);
                var rows = forecast.QuerySelectorAll(".grid");
                Assert.Contains("GB", rows[0].Children[0].TextContent);
                Assert.Contains("82%", rows[1].Children[0].TextContent);
                Assert.Contains("DET", rows[0].Children[2].TextContent);
                Assert.Contains("74%", rows[1].Children[2].TextContent);
            }
            else
            {
                var card = doc.QuerySelector($"a[href='/game/{game.Id}']")!;
                Assert.NotNull(card); Assert.DoesNotContain("Hype unavailable", card.TextContent);
                Assert.Matches(@"Hype\s*59", card.TextContent);
            }
            var html = doc.DocumentElement.OuterHtml;
            Assert.DoesNotContain("SchedulePublishedAt", html); Assert.DoesNotContain("CompletedGameIds", html);
            Assert.DoesNotContain("complete schedule results", html);
        }
        Assert.Equal(3, source.Requests.Count);
        string previous;
        using (var scope = wired.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            previous = ProGameEvidence.Parse((await db.Games.SingleAsync(g => g.Id == game.Id)).ProEvidenceJson)!.NflPlayoffLeverage!.ObservedAt.ToString("O");
            clock.Now = clock.Now.AddMinutes(15);
            source.ServerDate = clock.Now.UtcDateTime;
            source.Throw = true;
            await scope.ServiceProvider.GetRequiredService<ProCompetitionEvidenceService>().RefreshAsync(default);
            var stored = await db.Games.SingleAsync(g => g.Id == game.Id);
            var persisted = ProGameEvidence.Parse(stored.ProEvidenceJson)!;
            Assert.Equal(previous, persisted.NflPlayoffLeverage!.ObservedAt.ToString("O"));
            Assert.True((await scope.ServiceProvider.GetRequiredService<HypeScoreService>().GetHypeComponentsAsync(stored, null)).StakesAvailable);
        }
        source.Throw = false;
        source.Transform = (path, body) => path.EndsWith("packerswhatif.html") ? body.Replace("Next Game - Lions", "Next Game - Ravens") : body;
        using (var scope = wired.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ProCompetitionEvidenceService>().RefreshAsync(default);
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var stored = await db.Games.SingleAsync(g => g.Id == game.Id);
            Assert.Null(ProGameEvidence.Parse(stored.ProEvidenceJson)!.NflPlayoffLeverage!.AwayOdds);
            Assert.False((await scope.ServiceProvider.GetRequiredService<HypeScoreService>().GetHypeComponentsAsync(stored, null)).StakesAvailable);
        }
        var calls = source.Requests.Count;
        var missing = await BodyText(await browser.GetAsync("/game/" + game.Id));
        Assert.Contains("Hype unavailable", missing);
        Assert.Contains("Stakes need current win/loss playoff scenarios for Green Bay Packers", missing);
        Assert.Contains("Closeness80", missing);
        Assert.DoesNotContain("QualityUnavailable", missing);
        Assert.Equal(calls, source.Requests.Count);
        source.Transform = (_, body) => body;
        using (var scope = wired.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ProCompetitionEvidenceService>().RefreshAsync(default);
        clock.Now = clock.Now.AddHours(1);
        using (var scope = wired.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var stored = await db.Games.SingleAsync(g => g.Id == game.Id);
            var evidence = ProGameEvidence.Parse(stored.ProEvidenceJson)!;
            evidence.ObservedAt = clock.Now.UtcDateTime;
            stored.ProEvidenceJson = JsonSerializer.Serialize(evidence);
            var odds = await db.PregameOdds.SingleAsync(o => o.GameId == game.Id); odds.FetchedAt = clock.Now.UtcDateTime;
            await db.SaveChangesAsync();
            var expired = await scope.ServiceProvider.GetRequiredService<HypeScoreService>().GetHypeComponentsAsync(stored, null);
            Assert.False(expired.StakesAvailable); Assert.True(expired.QualityAvailable); Assert.True(expired.ClosenessAvailable);
        }
        missing = await BodyText(await browser.GetAsync("/game/" + game.Id));
        Assert.Contains("Hype unavailable", missing);
        Assert.Contains("Stakes need current win/loss playoff scenarios", missing);
        Assert.DoesNotContain("Playoff chances by result", missing);
    }
}
