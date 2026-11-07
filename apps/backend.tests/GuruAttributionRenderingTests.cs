using AngleSharp.Html.Parser;
using backend.GraphQL.Types;
using backend.Models;
using backend.Services;
using Backend.Rendering;
using Microsoft.FSharp.Collections;
using Xunit;

namespace backend.tests;

public sealed class GuruAttributionRenderingTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 18, 0, 0, DateTimeKind.Utc);
    private static List<Game> Games() => Enumerable.Range(0, 3).Select(index => new Game
    {
        Id = "synthetic-attribution-" + index, Sport = "nhl", Status = "scheduled", SeasonYear = 2027, SeasonType = 2,
        HomeTeam = "Synthetic Philadelphia", AwayTeam = "Synthetic Pittsburgh", HomeTeamAbbreviation = "PHI", AwayTeamAbbreviation = "PIT",
        ScheduledStart = Now.AddHours(index + 1)
    }).ToList();

    private static string RenderList(string page, List<Game> games, Dictionary<string, double?> scores, Dictionary<string, DerivedHypeCredit> credits)
    {
        var odds = new Dictionary<string, OddsSnapshot>();
        var excitement = new Dictionary<string, double?>();
        var ranks = new Dictionary<string, TeamRankInfo>();
        var content = page == "home"
            ? HomePageRenderer.RenderWithHypeCredits(games, odds, excitement, scores, ranks, credits)
            : LeaguePageRenderer.RenderWithHypeCredits(Sport.NHL, LeagueConfig.getLeagueConfig(Sport.NHL), games, odds, excitement, scores, null, ranks, null, null, null, credits);
        return "<!doctype html>" + Layout.renderPage("Synthetic attribution", "Synthetic games", null, content);
    }

    private static void Artifact(string page, string html)
    {
        if (Environment.GetEnvironmentVariable("GURU_CREDIT_ARTIFACT_DIR") is not string directory) return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, page + ".html"), html);
    }

    [Theory]
    [InlineData("home")]
    [InlineData("league")]
    public void Guru_credit_is_rendered_once_after_all_games_not_beneath_each_card(string page)
    {
        var games = Games();
        var scores = games.ToDictionary(g => g.Id, _ => (double?)56);
        var credits = games.ToDictionary(g => g.Id, _ => new DerivedHypeCredit(FSharpList<string>.Empty, NhlPowerRankingsGuruService.Source));
        var html = RenderList(page, games, scores, credits);
        Artifact(page, html);
        using var document = new HtmlParser().ParseDocument(html);
        var link = Assert.Single(document.QuerySelectorAll($"a[href='{NhlPowerRankingsGuruService.RankingsUrl}']"));
        var footer = document.QuerySelector("footer#quality-credit");
        Assert.NotNull(footer);
        Assert.Same(footer, footer.ParentElement!.LastElementChild);
        Assert.True(footer.Contains(link));
        Assert.Contains("Quality and hype use Power Rankings Guru team-strength rankings.", footer.TextContent);
        foreach (var game in games)
        {
            Assert.Null(document.QuerySelector("#quality-credit-" + game.Id));
            Assert.NotNull(document.QuerySelector($"a[href='/game/{game.Id}']"));
            Assert.Contains("56", document.QuerySelector("#excitement-" + game.Id)!.TextContent);
        }
    }

    [Theory]
    [InlineData("home", "missing-credit")]
    [InlineData("league", "missing-credit")]
    [InlineData("home", "other-source")]
    [InlineData("league", "other-source")]
    [InlineData("home", "off-page")]
    [InlineData("league", "off-page")]
    [InlineData("home", "missing-score")]
    [InlineData("league", "missing-score")]
    [InlineData("home", "nonfinite-score")]
    [InlineData("league", "nonfinite-score")]
    [InlineData("league", "live")]
    [InlineData("home", "historical")]
    [InlineData("league", "historical")]
    [InlineData("home", "empty")]
    [InlineData("league", "empty")]
    public void Page_credit_requires_an_actual_displayed_Guru_supported_score(string page, string missing)
    {
        var games = Games();
        var scores = games.ToDictionary(g => g.Id, _ => (double?)56);
        var credits = games.ToDictionary(g => g.Id, _ => new DerivedHypeCredit(FSharpList<string>.Empty, NhlPowerRankingsGuruService.Source));
        switch (missing)
        {
            case "missing-credit": credits.Clear(); break;
            case "other-source": foreach (var game in games) credits[game.Id] = new(FSharpList<string>.Empty, MoneyPuckRankings.Source); break;
            case "off-page": credits.Clear(); credits["not-on-this-page"] = new(FSharpList<string>.Empty, NhlPowerRankingsGuruService.Source); break;
            case "missing-score": foreach (var game in games) scores[game.Id] = null; break;
            case "nonfinite-score": foreach (var game in games) scores[game.Id] = double.NaN; break;
            case "live": foreach (var game in games) game.Status = "in_progress"; break;
            case "historical": foreach (var game in games) game.IsHistoricalImport = true; break;
            case "empty": games.Clear(); break;
        }
        using var document = new HtmlParser().ParseDocument(RenderList(page, games, scores, credits));
        Assert.Empty(document.QuerySelectorAll($"a[href='{NhlPowerRankingsGuruService.RankingsUrl}']"));
        Assert.Null(document.QuerySelector("#quality-credit"));
    }

    [Theory]
    [InlineData("home")]
    [InlineData("league")]
    public void Combining_Guru_credit_does_not_move_team_specific_MoneyPuck_model_disclosures(string page)
    {
        var games = Games();
        var scores = games.ToDictionary(g => g.Id, _ => (double?)0);
        var credits = games.ToDictionary(g => g.Id, g => new DerivedHypeCredit(ListModule.OfSeq(new[] { g.HomeTeamAbbreviation }),
            g == games[0] ? NhlPowerRankingsGuruService.Source : MoneyPuckRankings.Source));
        using var document = new HtmlParser().ParseDocument(RenderList(page, games, scores, credits));
        Assert.Single(document.QuerySelectorAll("footer#quality-credit"));
        foreach (var game in games)
        {
            var disclosure = document.QuerySelector("#hype-credit-" + game.Id);
            Assert.NotNull(disclosure);
            Assert.Contains("Stakes and hype use a model estimate for PHI", disclosure.TextContent);
            Assert.Contains("0", document.QuerySelector("#excitement-" + game.Id)!.TextContent);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Detail_credit_is_once_at_the_bottom_and_describes_only_the_available_metrics(bool hasHype)
    {
        var game = Games()[0];
        var components = new HypeComponents
        {
            StakesAvailable = hasHype, QualityAvailable = true, ClosenessAvailable = true,
            QualitySource = NhlPowerRankingsGuruService.Source, QualityScore = 79, ClosenessScore = 80, StakesScore = 1
        };
        var content = GameDetailRenderer.RenderAtTime(Now, game, new List<OddsSnapshot>(), null, components, hasHype ? 56 : null, null, null);
        var html = "<!doctype html>" + Layout.renderPage("Synthetic detail", "Synthetic game", null, content);
        Artifact(hasHype ? "detail" : "detail-partial", html);
        using var document = new HtmlParser().ParseDocument(html);
        var footer = document.QuerySelector("footer#quality-credit");
        Assert.NotNull(footer);
        Assert.Same(footer, footer.ParentElement!.LastElementChild);
        Assert.Single(document.QuerySelectorAll($"a[href='{NhlPowerRankingsGuruService.RankingsUrl}']"));
        Assert.Null(document.QuerySelector("#detail-hype #quality-credit"));
        Assert.StartsWith(hasHype ? "Quality and hype use " : "Quality uses ", footer.TextContent);
    }
}
