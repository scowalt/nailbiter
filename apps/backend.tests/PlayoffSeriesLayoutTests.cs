using System.Text.Json.Nodes;
using AngleSharp.Dom;
using backend.Data;
using backend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.tests;

public sealed class PlayoffSeriesLayoutTests : IAsyncLifetime
{
    private readonly PlayoffSeriesFixture fixture = new();
    private const string Id = "401907973";
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();

    [Theory]
    [InlineData(2, 1, "NLDS - Game 4", "ATL leads 2–1", "NLDS · Game 4 · Best of 5")]
    [InlineData(1, 2, "NLDS - Game 4", "PHI leads 2–1", "NLDS · Game 4 · Best of 5")]
    [InlineData(1, 1, "NLDS - Game 3", "Series tied 1–1", "NLDS · Game 3 · Best of 5")]
    [InlineData(0, 0, "NLDS - Game 1", "Series tied 0–0", "NLDS · Game 1 · Best of 5")]
    [InlineData(2, 2, "NLDS - Game 5", "Series tied 2–2", "NLDS · Game 5 · Best of 5")]
    [InlineData(1, 2, "", "PHI leads 2–1", "Best of 5")]
    public async Task Scheduled_standing_is_leader_first_or_tied_and_joins_only_verified_context_independently_of_odds(int homeWins, int awayWins, string note, string standing, string context)
    {
        ConfigureSeries(fixture.Provider.Board["events"]![0]!["competitions"]![0]!, 5, homeWins, awayWins, note);
        await fixture.CollectAsync();
        using var page = await fixture.HomeAsync();
        var card = page.QuerySelector($"a[href='/game/{Id}']")!;
        Assert.Equal(standing, card.QuerySelector($"#series-{Id}")!.Children[0].TextContent);
        Assert.Equal(context, card.QuerySelector($"#series-{Id}")!.Children[1].TextContent);
        Assert.Equal("Hype unavailable—", card.QuerySelector($"#excitement-{Id}")!.TextContent);
        Assert.Empty(card.QuerySelector($"#probbar-{Id}")!.Children);
        Assert.Equal("", card.QuerySelector($"#home-score-{Id}")!.TextContent);
        Assert.Equal("", card.QuerySelector($"#away-score-{Id}")!.TextContent);
    }

    [Fact]
    public async Task Scheduled_footer_preserves_known_hype_and_seeds_and_returns_to_identical_baseline_when_series_is_unavailable()
    {
        var board = fixture.Provider.Board.DeepClone();
        fixture.Provider.Board["events"]![0]!["competitions"]![0]!.AsObject().Remove("series");
        await fixture.CollectAsync();
        fixture.Provider.Board = board.DeepClone();
        using (var scope = fixture.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var game = await db.Games.SingleAsync();
            db.TeamRatings.AddRange(ProHypeTests.Ranks(game, 4, 8));
            db.TeamStandings.AddRange(new TeamStanding
            {
                Sport = "mlb", TeamAbbreviation = "ATL", Season = 2026, ConferenceRank = 2
            }, new TeamStanding
            {
                Sport = "mlb", TeamAbbreviation = "PHI", Season = 2026, ConferenceRank = 5
            });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<backend.Services.ProCompetitionEvidenceService>().RefreshAsync(default);
            await scope.ServiceProvider.GetRequiredService<backend.Services.PregameOddsService>().RefreshPregameOddsForSportAsync("mlb", null);
        }
        using var baseline = await fixture.HomeAsync();
        var original = baseline.QuerySelector($"a[href='/game/{Id}']")!;
        Assert.Contains("Hype87", original.TextContent);
        fixture.Provider.Board = board;
        fixture.Host.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.CollectAsync();
        using (var page = await fixture.HomeAsync())
        {
            var card = page.QuerySelector($"a[href='/game/{Id}']")!;
            Assert.Contains("Series tied 1–1", card.TextContent);
            Assert.Equal(original.QuerySelector($"#scores-{Id}")!.OuterHtml, card.QuerySelector($"#scores-{Id}")!.OuterHtml);
            Assert.Contains("#2 seed", card.TextContent);
            Assert.Contains("#5 seed", card.TextContent);
            Assert.Equal(original.QuerySelector($"#excitement-{Id}")!.TextContent, card.QuerySelector($"#excitement-{Id}")!.TextContent);
        }
        fixture.Host.Clock.Advance(TimeSpan.FromMinutes(1));
        fixture.Provider.Board["events"]![0]!["competitions"]![0]!.AsObject().Remove("series");
        await fixture.CollectAsync();
        using var unavailable = await fixture.HomeAsync();
        Assert.Equal(original.OuterHtml, unavailable.QuerySelector($"a[href='/game/{Id}']")!.OuterHtml);
        Assert.Empty(unavailable.QuerySelectorAll("select, [id^='prototype'], [id^='series-']"));
    }

    [Fact]
    public async Task Initial_http_html_escapes_source_team_text_and_never_promotes_unverified_round_markup()
    {
        var competition = fixture.Provider.Board["events"]![0]!["competitions"]![0]!;
        ConfigureSeries(competition, 5, 2, 1, "<img src=x onerror=alert(1)> - Game 4");
        competition["competitors"]![0]!["team"]!["abbreviation"] = "A<B&\"";
        await fixture.CollectAsync();
        using var page = await fixture.HomeAsync();
        var series = page.QuerySelector($"a[href='/game/{Id}'] #series-{Id}")!;
        Assert.NotNull(series);
        Assert.Equal("A<B&\" leads 2–1", series.Children[0].TextContent);
        Assert.Equal("Best of 5", series.Children[1].TextContent);
        Assert.Empty(series.QuerySelectorAll("img, script, b, [onerror]"));
        Assert.Contains("&lt;", series.InnerHtml);
        Assert.Contains("&amp;", series.InnerHtml);
    }

    private static void ConfigureSeries(JsonNode competition, int bestOf, int homeWins, int awayWins, string note)
    {
        competition["series"]!["totalCompetitions"] = bestOf;
        competition["series"]!["competitors"]![0]!["wins"] = homeWins;
        competition["series"]!["competitors"]![1]!["wins"] = awayWins;
        competition["notes"]![0]!["headline"] = note;
    }

    [Theory]
    [InlineData(3, 2, 0, "NLWC - Game 2", "ATL wins series 2–0", "NLWC · Game 2 · Best of 3")]
    [InlineData(5, 1, 3, "NLDS - Game 4", "PHI wins series 3–1", "NLDS · Game 4 · Best of 5")]
    [InlineData(7, 2, 2, "World Series - Game 4", "Series tied 2–2", "World Series · Game 4 · Best of 7")]
    public async Task Completed_cards_use_confirmed_threshold_wording_and_keep_final_sparkline(int bestOf, int homeWins, int awayWins, string note, string standing, string context)
    {
        var item = fixture.Provider.Board["events"]![0]!;
        var competition = item["competitions"]![0]!;
        ConfigureSeries(competition, bestOf, homeWins, awayWins, note);
        competition["series"]!["completed"] = homeWins == bestOf / 2 + 1 || awayWins == bestOf / 2 + 1;
        foreach (var status in new[] { item["status"]!, competition["status"]! })
        {
            status["type"]!["state"] = "post";
            status["type"]!["name"] = "STATUS_FINAL";
            status["type"]!["completed"] = true;
        }
        var homeWinner = homeWins > awayWins;
        competition["competitors"]![0]!["winner"] = homeWinner;
        competition["competitors"]![1]!["winner"] = !homeWinner;
        competition["competitors"]![0]!["score"] = homeWinner ? "8" : "3";
        competition["competitors"]![1]!["score"] = homeWinner ? "3" : "8";
        await fixture.CollectAsync();
        using (var scope = fixture.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            db.OddsSnapshots.AddRange(new[] { .4, .6, .55 }.Select((p, i) => new OddsSnapshot
            {
                GameId = Id, Timestamp = fixture.Host.Clock.GetUtcNow().UtcDateTime.AddSeconds(i - 3),
                HomeWinProbability = p, AwayWinProbability = 1 - p, Period = 9, GameClock = "0:00"
            }));
            await db.SaveChangesAsync();
        }
        using var page = await fixture.HomeAsync();
        var card = page.QuerySelector($"a[href='/game/{Id}']")!;
        Assert.Equal(standing, card.QuerySelector($"#series-{Id}")!.Children[0].TextContent);
        Assert.Equal(context, card.QuerySelector($"#series-{Id}")!.Children[1].TextContent);
        Assert.Equal("true", card.QuerySelector("nb-sparkline")!.GetAttribute("data-live-final"));
        Assert.Equal("Excitement4", card.QuerySelector($"#excitement-{Id}")!.TextContent);
        Assert.Equal(homeWinner ? "8" : "3", card.QuerySelector($"#home-score-{Id}")!.TextContent);
        Assert.Equal(homeWinner ? "3" : "8", card.QuerySelector($"#away-score-{Id}")!.TextContent);
        Assert.Equal("Final", card.QuerySelector($"#status-{Id}")!.TextContent);
    }

    [Fact]
    public async Task Live_standing_sits_above_the_original_sparkline_outside_patch_targets_with_metrics_scores_and_navigation_intact()
    {
        var item = fixture.Provider.Board["events"]![0]!;
        var competition = item["competitions"]![0]!;
        foreach (var status in new[] { item["status"]!, competition["status"]! })
        {
            status["type"]!["state"] = "in";
            status["type"]!["name"] = "STATUS_IN_PROGRESS";
            status["type"]!["completed"] = false;
        }
        competition["competitors"]![0]!["score"] = "7";
        competition["competitors"]![1]!["score"] = "4";
        await fixture.CollectAsync();
        using (var scope = fixture.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            db.OddsSnapshots.AddRange(new[] { .4, .6, .55 }.Select((p, i) => new OddsSnapshot
            {
                GameId = Id, Timestamp = fixture.Host.Clock.GetUtcNow().UtcDateTime.AddSeconds(i - 3),
                HomeWinProbability = p, AwayWinProbability = 1 - p, Period = 5, GameClock = "0:00"
            }));
            await db.SaveChangesAsync();
        }
        var requests = fixture.Provider.Requests;
        using var page = await fixture.HomeAsync();
        var card = page.QuerySelector($"a[href='/game/{Id}']")!;
        Assert.NotNull(card);
        Assert.Contains("Series tied 1–1", card.TextContent);
        var series = card.QuerySelector($"#series-{Id}")!;
        Assert.Equal("NLWC · Game 3 · Best of 3", series.Children[1].TextContent);
        var sparkline = card.QuerySelector("nb-sparkline")!;
        Assert.Equal(Id, sparkline.GetAttribute("game-id"));
        Assert.Equal("false", sparkline.GetAttribute("data-live-final"));
        Assert.True(series.CompareDocumentPosition(sparkline).HasFlag(DocumentPositions.Following));
        foreach (var target in new[] { "scores", "status", "clock", "probbar", "excitement" })
            Assert.Null(series.Closest($"#{target}-{Id}"));
        Assert.Equal("Excitement4", card.QuerySelector($"#excitement-{Id}")!.TextContent);
        Assert.Equal("7", card.QuerySelector($"#home-score-{Id}")!.TextContent);
        Assert.Equal("4", card.QuerySelector($"#away-score-{Id}")!.TextContent);
        Assert.Contains("55%", card.QuerySelector($"#probbar-{Id}")!.TextContent);
        Assert.Contains("5", card.QuerySelector($"#clock-{Id}")!.TextContent);
        Assert.Equal(requests, fixture.Provider.Requests);
        Assert.Empty(card.QuerySelectorAll("button, select, input"));
    }
}
