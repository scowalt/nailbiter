using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using backend.Data;
using backend.Scoring;
using backend.Models;
using backend.Services;
using backend.Services.Espn;
using backend.Services.Kalshi;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace backend.tests;

public sealed partial class NflPlayoffPageTests
{
    [Fact]
    public async Task Synthetic_Colts_Steelers_Kalshi_quotes_restore_pregame_closeness_league_and_detail()
    {
        clock.Now = new DateTimeOffset(2026, 10, 4, 19, 56, 0, TimeSpan.Zero);
        var capture = ProHypeTests.Fixture("nfl-pregame-401872985");
        var ev = capture.GetProperty("board").GetProperty("body").GetProperty("events")[0];
        Assert.False(ev.GetProperty("competitions")[0].TryGetProperty("odds", out _));
        var game = new Game
        {
            Id = ev.GetProperty("id").GetString()!, Sport = "nfl", SeasonYear = 2026, SeasonType = 2, WeekNumber = 5,
            HomeTeam = "Pittsburgh Steelers", AwayTeam = "Indianapolis Colts", HomeTeamAbbreviation = "PIT", AwayTeamAbbreviation = "IND",
            HomeTeamId = "23", AwayTeamId = "11", Status = "scheduled", ScheduledStart = ev.GetProperty("date").GetDateTime()
        };
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var context = ProHypeTests.Evidence(game); context.ObservedAt = clock.Now.UtcDateTime;
        context.NflPlayoffLeverage = NflPlayoffLeverageTests.SyntheticEvidence(game, clock.Now.UtcDateTime);
        game.ProEvidenceJson = JsonSerializer.Serialize(context);
        var ranks = ProHypeTests.Ranks(game, 20, 24);
        foreach (var rank in ranks) rank.FetchedAt = clock.Now.UtcDateTime;
        db.Games.Add(game); db.TeamRatings.AddRange(ranks);
        foreach (var team in new[] { "PIT", "IND" })
            db.TeamRatings.Add(new TeamRating { Sport = "nfl", TeamAbbreviation = team, Season = 2026, SourceSeason = 2026,
                Source = "kalshi", PlayoffProbability = .5, FetchedAt = clock.Now.UtcDateTime });
        await db.SaveChangesAsync();
        async Task<HypeComponents> Components()
        {
            using var requestScope = app.Services.CreateScope();
            return await requestScope.ServiceProvider.GetRequiredService<HypeScoreService>().GetHypeComponentsAsync(game, null);
        }
        var before = await Components();
        Assert.True(before.StakesAvailable); Assert.True(before.QualityAvailable); Assert.False(before.ClosenessAvailable);
        var detail = await BodyText(await client.GetAsync("/game/" + game.Id));
        Assert.Contains("Hype unavailable", detail); Assert.Contains("Current game-winner odds are unavailable.", detail);
        Assert.Contains("ClosenessUnavailable", detail);

        var board = JsonNode.Parse(capture.GetProperty("board").GetProperty("body").GetRawText())!;
        var kalshiBody = capture.GetProperty("kalshi").GetProperty("body").GetRawText();
        var kalshiFailed = false;
        using var handler = new ProFixtureHandler(url =>
        {
            if (url.Contains("/markets?", StringComparison.Ordinal))
            {
                Assert.Contains("status=open", url);
                return kalshiFailed ? new(HttpStatusCode.ServiceUnavailable)
                    : new(HttpStatusCode.OK) { Content = new StringContent(kalshiBody) };
            }
            Assert.Contains("/scoreboard", url);
            return new(HttpStatusCode.OK) { Content = new StringContent(url.EndsWith("dates=20261011", StringComparison.Ordinal)
                ? board.ToJsonString() : "{\"events\":[]}") };
        });
        using var http = new HttpClient(handler);
        await using var replayApp = CreateAppWithSettings(new(), configureServices: services =>
        {
            services.RemoveAll<EspnOddsService>(); services.RemoveAll<KalshiOddsService>();
            services.AddScoped(sp => new EspnOddsService(http, NullLogger<EspnOddsService>.Instance, sp, null!, null!));
            services.AddScoped(_ => new KalshiOddsService(http, NullLogger<KalshiOddsService>.Instance));
        });
        using var replayClient = replayApp.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        async Task Refresh()
        {
            using var importScope = replayApp.Services.CreateScope();
            await importScope.ServiceProvider.GetRequiredService<PregameOddsService>().RefreshPregameOddsForSportAsync("nfl", null);
        }
        Task<PregameOdds> Quote() => db.PregameOdds.AsNoTracking().SingleAsync(o => o.GameId == game.Id);
        await Refresh();
        var after = await Components();
        Assert.True(after.ClosenessAvailable, after.ClosenessExplanation);
        var quote = await db.PregameOdds.AsNoTracking().SingleAsync(o => o.GameId == game.Id);
        Assert.Equal("kalshi", quote.Source);
        Assert.Equal(.61 / 1.03, quote.HomeWinProbability, 12);
        Assert.Equal(.42 / 1.03, quote.AwayWinProbability, 12);
        Assert.True(after.IsComplete, after.ClosenessExplanation);
        Assert.Equal(before.StakesScore, after.StakesScore); Assert.Equal(before.QualityScore, after.QualityScore);
        Assert.Equal(81.55339805825243, after.ClosenessScore, 10);
        Assert.Contains("kalshi", after.ClosenessExplanation);
        foreach (var path in new[] { "/nfl?week=5&season=2026&type=2", "/game/" + game.Id })
        {
            using var doc = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(await replayClient.GetStringAsync(path), CancellationToken.None);
            var text = path.StartsWith("/game/", StringComparison.Ordinal) ? doc.Body!.TextContent : doc.QuerySelector($"a[href='/game/{game.Id}']")!.TextContent;
            Assert.DoesNotContain("Hype unavailable", text);
            if (path.StartsWith("/game/", StringComparison.Ordinal)) Assert.Contains("Closeness82", text);
        }

        board["events"]![0]!["competitions"]![0]!["odds"] = JsonNode.Parse("""
            [{"moneyline":{"home":{"close":{"odds":"-150"}},"away":{"close":{"odds":"+150"}}}}]
            """);
        clock.Now = clock.Now.AddMinutes(1); await Refresh();
        Assert.Equal("kalshi", (await Quote()).Source);
        Assert.Equal(.61 / 1.03, (await Quote()).HomeWinProbability, 12);
        kalshiFailed = true; clock.Now = clock.Now.AddMinutes(1); await Refresh();
        quote = await Quote(); Assert.Equal("espn", quote.Source); Assert.Equal(.6, quote.HomeWinProbability, 12);
        Assert.Equal(clock.Now.UtcDateTime, quote.FetchedAt);
        kalshiFailed = false; clock.Now = clock.Now.AddMinutes(1); await Refresh();
        quote = await Quote(); Assert.Equal("kalshi", quote.Source);
        var observed = quote.FetchedAt;
        board["events"]![0]!["competitions"]![0]!.AsObject().Remove("odds");
        kalshiFailed = true; clock.Now = clock.Now.AddMinutes(1); await Refresh();
        quote = await Quote(); Assert.Equal(observed, quote.FetchedAt); Assert.Equal(2026, quote.SourceSeason);
        Assert.True(ProHypePolicy.EligibleOdds(game, quote, clock.Now.UtcDateTime));
        kalshiFailed = false; kalshiBody = "{\"markets\":[],\"cursor\":\"\"}";
        clock.Now = clock.Now.AddMinutes(1); await Refresh();
        quote = await Quote(); Assert.Null(quote.SourceSeason); Assert.Equal(clock.Now.UtcDateTime, quote.FetchedAt);
        Assert.False((await Components()).ClosenessAvailable);
        kalshiBody = capture.GetProperty("kalshi").GetProperty("body").GetRawText();
        clock.Now = clock.Now.AddMinutes(1); await Refresh();
        Assert.True((await Components()).ClosenessAvailable);
        Assert.Equal(1, await db.PregameOdds.CountAsync(o => o.GameId == game.Id));
        clock.Now = clock.Now.AddHours(1);
        Assert.False(ProHypePolicy.EligibleOdds(game, await Quote(), clock.Now.UtcDateTime));
        Assert.False((await Components()).ClosenessAvailable);
    }
}
