using System.Text.Json;
using System.Text.Json.Nodes;
using backend.Models;
using backend.Services;
using Xunit;

namespace backend.tests;

public sealed class NflForecastTitleTests
{
    internal const string FortyNinersTitle = "San Francisco Forty-Niners What If?";
    private static string Swap(string team) => team switch { "DET" => "SF", "SF" => "DET", _ => team };

    internal static void UseSanFrancisco(Game game)
    {
        game.HomeTeamAbbreviation = "SF";
        game.HomeTeam = "San Francisco 49ers";
    }

    internal static ProCompetitionEvidenceService.Schedule[] SwapSchedules(ProCompetitionEvidenceService.Schedule[] schedules) =>
        schedules.Select(s => s with { Team = Swap(s.Team), Events = s.Events.Select(e =>
        {
            var copy = JsonNode.Parse(e.GetRawText())!;
            foreach (var competitor in copy["competitions"]![0]!["competitors"]!.AsArray())
                competitor!["team"]!["abbreviation"] = Swap(competitor["team"]!["abbreviation"]!.GetValue<string>());
            return JsonSerializer.SerializeToElement(copy);
        }).ToArray() }).ToArray();

    internal static void UseNicknamePage(NflPlayoffLeverageTests.Handler handler)
    {
        handler.FixtureName = name => name == "forty-ninerswhatif.html" ? "lionswhatif.html" : name;
        var previous = handler.Transform;
        handler.Transform = (path, original) =>
        {
            var body = previous(path, original);
            if (path.EndsWith("nflschedule.html"))
                return body.Replace("#lions", "#controlled-swap").Replace("#forty-niners", "#lions").Replace("#controlled-swap", "#forty-niners");
            if (path.EndsWith("forty-ninerswhatif.html")) return body.Replace("Detroit Lions What If?", FortyNinersTitle);
            return body.Replace("Next Game - Lions", "Next Game - 49ers");
        };
    }

    [Theory]
    [InlineData(0, FortyNinersTitle)]
    [InlineData(20, FortyNinersTitle)]
    [InlineData(0, "San Francisco 49ers What If?")]
    [InlineData(20, "San Francisco 49ers What If?")]
    public async Task The_explicit_San_Francisco_titles_identify_the_same_team(int minutes, string title)
    {
        var game = NflCorroboratedKickoffTests.Game(minutes);
        UseSanFrancisco(game);
        var handler = new NflPlayoffLeverageTests.Handler();
        UseNicknamePage(handler);
        var previous = handler.Transform;
        handler.Transform = (path, body) => previous(path, body).Replace(FortyNinersTitle, title);
        var evidence = (await NflPlayoffLeverageTests.Collector(handler).CollectAsync([game],
            SwapSchedules(NflCorroboratedKickoffTests.Schedules(game)), default))[game.Id]!;
        Assert.True(evidence.ScheduleVerified, evidence.Reason);
        Assert.NotNull(evidence.HomeOdds);
        Assert.EndsWith("/forty-ninerswhatif.html", evidence.HomeOdds.Reference);
        Assert.NotNull(evidence.AwayOdds);
        Assert.Equal(34.4, NflCorroboratedKickoffTests.Score(game, evidence).Stakes.Value!.Value, 8);
        Assert.Equal(64.8733971774, NflCorroboratedKickoffTests.Score(game, evidence).Total!.Value, 10);
        Assert.Equal(minutes == 0 ? 1 : 2, evidence.Version);
    }

    [Theory]
    [InlineData("foreign-title")]
    [InlineData("title-suffix")]
    [InlineData("week")]
    [InlineData("generation")]
    [InlineData("opponent")]
    [InlineData("record")]
    public async Task A_recognized_nickname_does_not_bypass_other_team_or_forecast_checks(string problem)
    {
        var game = NflCorroboratedKickoffTests.Game(20);
        UseSanFrancisco(game);
        var handler = new NflPlayoffLeverageTests.Handler();
        UseNicknamePage(handler);
        var previous = handler.Transform;
        handler.Transform = (path, body) =>
        {
            var transformed = previous(path, body);
            if (!path.EndsWith("forty-ninerswhatif.html")) return transformed;
            return problem switch
            {
                "foreign-title" => transformed.Replace(FortyNinersTitle, "Seattle Seahawks What If?"),
                "title-suffix" => transformed.Replace(FortyNinersTitle, FortyNinersTitle + " Other"),
                "week" => transformed.Replace("Week 7 of 18", "Week 8 of 18"),
                "generation" => transformed.Replace("Synthetic publication batch A", "Synthetic publication batch B"),
                "opponent" => transformed.Replace("Next Game - Packers", "Next Game - Ravens"),
                _ => transformed.Replace("<td>Win Next Game</td>", "<td>Win Next Game</td><td>999</td>")
            };
        };
        var evidence = (await NflPlayoffLeverageTests.Collector(handler).CollectAsync([game],
            SwapSchedules(NflCorroboratedKickoffTests.Schedules(game)), default))[game.Id]!;
        Assert.Null(evidence.HomeOdds);
        Assert.NotNull(evidence.AwayOdds);
        Assert.Null(NflCorroboratedKickoffTests.Score(game, evidence).Total);
    }

    [Fact]
    public async Task The_Forty_Niners_title_cannot_authenticate_an_unrelated_teams_page()
    {
        var game = NflPlayoffLeverageTests.Game();
        var handler = new NflPlayoffLeverageTests.Handler { Transform = (path, body) => path.EndsWith("lionswhatif.html")
            ? body.Replace("Detroit Lions What If?", FortyNinersTitle) : body };
        var evidence = (await NflPlayoffLeverageTests.Collector(handler).CollectAsync([game], NflPlayoffLeverageTests.Schedules(), default))[game.Id]!;
        Assert.Null(evidence.HomeOdds);
        Assert.Null(NflCorroboratedKickoffTests.Score(game, evidence).Total);
    }
}
