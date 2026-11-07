using AngleSharp.Html.Parser;
using backend.Models;
using backend.Services;
using Backend.Rendering;
using Microsoft.FSharp.Collections;
using Xunit;

namespace backend.tests;

public sealed class LiveSectionRenderingTests
{
    private static Game Live(string id, int hour) => new()
    {
        Id = id, Sport = "nhl", Status = "in_progress", SeasonYear = 2027, SeasonType = 2,
        HomeTeam = "Synthetic Philadelphia", AwayTeam = "Synthetic Pittsburgh", HomeTeamAbbreviation = "PHI", AwayTeamAbbreviation = "PIT",
        ScheduledStart = new DateTime(2026, 10, 11, hour, 0, 0, DateTimeKind.Utc)
    };

    private static AngleSharp.Html.Dom.IHtmlDocument Render(List<Game> games, Dictionary<string, double?> excitement,
        Dictionary<string, double?> hype, Dictionary<string, DerivedHypeCredit> credits)
    {
        var content = HomePageRenderer.RenderWithHypeCredits(games, new Dictionary<string, OddsSnapshot>(), excitement, hype,
            new Dictionary<string, TeamRankInfo>(), credits);
        return new HtmlParser().ParseDocument("<!doctype html>" + Layout.renderPage("Synthetic live", "Synthetic games", null, content));
    }

    [Fact]
    public void Live_now_ranks_by_the_mean_of_start_hype_and_excitement_with_unscored_games_last_in_start_order()
    {
        var games = new List<Game> { Live("unscored-early", 17), Live("hyped-dull", 18), Live("unscored-late", 19),
            Live("modest-thriller", 20), Live("excitement-only", 21), Live("hype-only", 22) };
        var excitement = new Dictionary<string, double?>
        {
            ["hyped-dull"] = 10, ["modest-thriller"] = 80, ["excitement-only"] = 60, ["unscored-late"] = double.NaN
        };
        var hype = new Dictionary<string, double?> { ["hyped-dull"] = 90, ["modest-thriller"] = 30, ["hype-only"] = 52 };
        using var page = Render(games, excitement, hype, []);
        Assert.Equal(["excitement-only", "modest-thriller", "hype-only", "hyped-dull", "unscored-early", "unscored-late"],
            page.QuerySelectorAll("#home-section-live a[href^='/game/']").Select(a => a.GetAttribute("href")![6..]));
        Assert.Equal("Hype90", page.QuerySelector("#hype-hyped-dull")!.TextContent.Replace(" ", ""));
        Assert.Equal("Excitement10", page.QuerySelector("#excitement-hyped-dull")!.TextContent.Replace(" ", ""));
        Assert.Null(page.QuerySelector("#hype-excitement-only"));
    }

    [Fact]
    public void A_live_start_hype_keeps_its_Guru_and_MoneyPuck_credit()
    {
        var game = Live("credited", 18);
        var credit = new DerivedHypeCredit(ListModule.OfSeq(new[] { "PHI" }), NhlPowerRankingsGuruService.Source);
        using var page = Render([game], [], new() { [game.Id] = 61 }, new() { [game.Id] = credit });
        Assert.NotNull(page.QuerySelector("footer#quality-credit"));
        Assert.Contains("Stakes and hype use a model estimate for PHI", page.QuerySelector("#hype-credit-credited")!.TextContent);
    }
}
