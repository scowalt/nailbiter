using System.Net;
using System.Text.Json;
using AngleSharp.Html.Parser;
using backend.Data;
using backend.Models;
using backend.Services;
using backend.Services.Kalshi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace backend.tests;

public sealed partial class NflPlayoffPageTests
{
    [Fact]
    public async Task Low_interest_display_reaches_all_public_pages_without_changing_hype_or_qualification()
    {
        clock.Now = new DateTimeOffset(ProHypeTests.Now);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var game = await db.Games.SingleAsync(g => g.Id == "nfl-current");
        game.ScheduledStart = clock.Now.AddHours(4).UtcDateTime;
        var evidence = ProHypeTests.Evidence(game);
        evidence.NflPlayoffLeverage = NflPlayoffLeverageTests.SyntheticEvidence(game, clock.Now.UtcDateTime);
        game.ProEvidenceJson = JsonSerializer.Serialize(evidence);
        foreach (var row in await db.TeamRatings.Where(r => r.Sport == "nfl").ToListAsync())
        {
            row.PlayoffProbability = null;
            row.SourceSeason = 2026;
            row.FetchedAt = clock.Now.UtcDateTime;
        }
        var odds = await db.PregameOdds.SingleAsync(o => o.GameId == game.Id);
        odds.Source = "espn"; odds.SourceSeason = 2026; odds.ApplicableStart = game.ScheduledStart;
        odds.HomeTeamAbbreviation = "KC"; odds.AwayTeamAbbreviation = "BUF"; odds.FetchedAt = clock.Now.UtcDateTime;
        await db.SaveChangesAsync();
        var scorer = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
        var before = await scorer.GetHypeComponentsAsync(game, null);
        Assert.True(before.IsComplete);
        Assert.Null(before.HomePlayoffOdds); Assert.Null(before.AwayPlayoffOdds);
        var beforeTotal = scorer.CalculateFinalScore("nfl", before);
        var teams = "ARI ATL BAL BUF CAR CHI CIN CLE DAL DEN DET GB HOU IND JAX KC LAC LAR LV MIA MIN NE NO NYG NYJ PHI PIT SEA SF TB TEN WSH".Split(' ');
        var requests = 0;
        using var handler = new ProFixtureHandler(url =>
        {
            Assert.Equal("https://api.elections.kalshi.com/trade-api/v2/events/KXNFLPLAYOFF-27?with_nested_markets=true", url);
            requests++;
            var body = JsonSerializer.Serialize(new
            {
                @event = new
                {
                    event_ticker = "KXNFLPLAYOFF-27", series_ticker = "KXNFLPLAYOFF", sub_title = "2026-27",
                    markets = teams.Select(team => new
                    {
                        ticker = "KXNFLPLAYOFF-27-" + team, event_ticker = "KXNFLPLAYOFF-27", status = "active", result = "",
                        market_type = "binary", notional_value_dollars = "1.00",
                        yes_bid_dollars = team is "KC" or "BUF" ? "0.00" : "0.60",
                        yes_ask_dollars = team is "KC" or "BUF" ? "0.02" : "0.64",
                        yes_bid_size_fp = team is "KC" or "BUF" ? "0.00" : "0.50", yes_ask_size_fp = "1.25",
                        volume_fp = "0.00", volume_24h_fp = "0.00", close_time = clock.Now.AddMonths(3).UtcDateTime
                    })
                }
            });
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        using var http = new HttpClient(handler);
        var importer = new KalshiPlayoffRatingsService(http, NullLogger<KalshiPlayoffRatingsService>.Instance, app.Services, clock);
        Assert.Equal(30, await importer.RefreshAsync("nfl", 2026, default));
        Assert.Equal(1, requests);
        db.ChangeTracker.Clear();
        var after = await scorer.GetHypeComponentsAsync(game, null);
        Assert.Null(after.HomePlayoffOdds); Assert.Null(after.AwayPlayoffOdds);
        Assert.False(after.HomeQualificationIsFinal); Assert.False(after.AwayQualificationIsFinal);
        Assert.Equal(before.StakesScore, after.StakesScore);
        Assert.Equal(before.QualityScore, after.QualityScore);
        Assert.Equal(before.ClosenessScore, after.ClosenessScore);
        Assert.Equal(beforeTotal, scorer.CalculateFinalScore("nfl", after));
        foreach (var (page, path) in new[] { ("home", "/"), ("league", "/nfl?week=1&season=2026&type=2"), ("detail", "/game/nfl-current") })
        {
            var html = await client.GetStringAsync(path);
            using var document = await new HtmlParser().ParseDocumentAsync(html);
            var region = page == "detail" ? document.QuerySelector("#detail-comparison")! : document.QuerySelector("a[href='/game/nfl-current']")!;
            Assert.Equal(2, region.QuerySelectorAll("[aria-description='Low-interest display convention, not an exact probability.']").Length);
            if (page == "league") Assert.Contains("league-game-body-stacked", region.FirstElementChild!.ClassList);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(region.TextContent, "<1%").Count);
            Assert.DoesNotContain("0–2%", region.TextContent);
            Assert.DoesNotContain("Missed playoffs", region.TextContent);
            Assert.DoesNotContain("Clinched playoffs", region.TextContent);
            Assert.DoesNotContain("Hype unavailable", document.QuerySelector(page == "detail" ? "#detail-hype" : "#excitement-nfl-current")!.TextContent);
            if (Environment.GetEnvironmentVariable("NFL_LOW_INTEREST_ARTIFACT_DIR") is string directory)
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, page + ".html"), html);
            }
        }
        var rows = await db.TeamRatings.AsNoTracking().Where(r => r.Source == "kalshi" && (r.TeamAbbreviation == "KC" || r.TeamAbbreviation == "BUF")).ToListAsync();
        Assert.All(rows, row => { Assert.Null(row.PlayoffProbability); Assert.False(row.PlayoffProbabilityIsFinal); });
        Assert.All(rows, row => Assert.Equal("insufficient_depth", PlayoffQuoteEvidence.Parse(row.PlayoffEvidenceJson)!.ReasonCode));
        Assert.Equal(1, requests);
        game = await db.Games.SingleAsync(g => g.Id == "nfl-current");
        evidence.NflPlayoffLeverage = null;
        game.ProEvidenceJson = JsonSerializer.Serialize(evidence);
        await db.SaveChangesAsync();
        var missing = await scorer.GetHypeComponentsAsync(game, null);
        Assert.False(missing.StakesAvailable);
        Assert.False(double.IsFinite(scorer.CalculateFinalScore("nfl", missing)));
        using var unavailable = await new HtmlParser().ParseDocumentAsync(await client.GetStringAsync("/game/nfl-current"));
        Assert.Contains("Hype unavailable", unavailable.QuerySelector("#detail-hype")!.TextContent);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(unavailable.QuerySelector("#detail-comparison")!.TextContent, "<1%").Count);
    }
}
