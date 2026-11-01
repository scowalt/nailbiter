using System.Text.Json;
using System.Text.Json.Nodes;
using backend.Models;
using backend.Scoring;
using backend.Services;
using Xunit;

namespace backend.tests;

public sealed class MlbPregameEvidenceTests
{
    internal static readonly DateTime Now = new(2026, 10, 1, 19, 15, 0, DateTimeKind.Utc);
    internal static JsonElement Capture => ProHypeTests.Fixture("mlb-pregame-401907973");
    internal static Game SyntheticGame() => new()
    {
        Id = "401907973", Sport = "mlb", SeasonYear = 2026, SeasonType = 3,
        HomeTeam = "Atlanta Braves", AwayTeam = "Philadelphia Phillies",
        HomeTeamAbbreviation = "ATL", AwayTeamAbbreviation = "PHI",
        HomeTeamId = "15", AwayTeamId = "22", Status = "scheduled",
        ScheduledStart = new(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc)
    };

    private static ProGameEvidence Evidence(JsonElement ev)
    {
        var schedules = Capture.GetProperty("schedules").EnumerateArray().Select(c =>
        {
            var body = c.GetProperty("body");
            return Assert.IsType<ProCompetitionEvidenceService.Schedule>(ProCompetitionEvidenceService.ParseSchedule(
                body, "mlb", 2026, 3, body.GetProperty("team").GetProperty("abbreviation").GetString()!, Now));
        }).ToArray();
        return ProCompetitionEvidenceService.ParseEvent(SyntheticGame(), ev, schedules, Now, Capture.GetProperty("board").GetProperty("url").GetString()!);
    }

    [Fact]
    public void Synthetic_MLB_scheduled_first_inning_has_winner_take_all_stakes()
    {
        var ev = Capture.GetProperty("board").GetProperty("body").GetProperty("events")[0];
        var context = Evidence(ev);
        Assert.Equal(1, context.Round);
        Assert.True(context.HomeElimination);
        Assert.True(context.AwayElimination);
        Assert.True(context.VerifiedUnstarted);
        var result = ProHypePolicy.Calculate(SyntheticGame(), context, [], new(null, false, null), new(null, false, null),
            null, ["401907965", "401907972"], Now);
        Assert.Equal(85, result.Stakes.Value);
    }

    [Theory]
    [InlineData("mlb", 0, true)]
    [InlineData("mlb", 1, true)]
    [InlineData("mlb", 2, false)]
    [InlineData("mlb", -1, false)]
    [InlineData("mlb", null, false)]
    [InlineData("nfl", 0, true)]
    [InlineData("nfl", 1, false)]
    [InlineData("nba", 0, true)]
    [InlineData("nba", 1, false)]
    [InlineData("nhl", 0, true)]
    [InlineData("nhl", 1, false)]
    public void First_period_exception_is_only_for_MLB(string sport, int? period, bool expected)
    {
        Assert.Equal(expected, EspnPregameStatus.IsUnstarted(sport, "STATUS_SCHEDULED", "pre", false, period));
    }

    [Fact]
    public void Synthetic_period_zero_control_is_already_accepted()
    {
        var ev = JsonNode.Parse(Capture.GetProperty("board").GetProperty("body").GetProperty("events")[0].GetRawText())!;
        ev["competitions"]![0]!["status"]!["period"] = 0;
        Assert.True(Evidence(JsonSerializer.SerializeToElement(ev)).VerifiedUnstarted);
    }

    [Theory]
    [InlineData("later-inning")]
    [InlineData("live-first-inning")]
    [InlineData("suspended")]
    [InlineData("postponed")]
    [InlineData("completed")]
    [InlineData("scored")]
    public void Synthetic_started_or_contradictory_MLB_context_is_rejected(string variant)
    {
        var ev = JsonNode.Parse(Capture.GetProperty("board").GetProperty("body").GetProperty("events")[0].GetRawText())!;
        Invalidate(ev, variant);
        Assert.False(Evidence(JsonSerializer.SerializeToElement(ev)).VerifiedUnstarted);
    }

    internal static void Invalidate(JsonNode ev, string variant)
    {
        var c = ev["competitions"]![0]!;
        foreach (var status in new[] { ev["status"]!, c["status"]! })
        {
            switch (variant)
            {
                case "later-inning": status["period"] = 2; break;
                case "live-first-inning": status["type"]!["name"] = "STATUS_IN_PROGRESS"; status["type"]!["state"] = "in"; break;
                case "suspended": status["type"]!["name"] = "STATUS_SUSPENDED"; break;
                case "postponed": status["type"]!["name"] = "STATUS_POSTPONED"; break;
                case "completed": status["type"]!["completed"] = true; break;
                case "scored": c["competitors"]![0]!["score"] = "1"; break;
            }
        }
    }
}
