using System.Text.Json;
using System.Text.Json.Nodes;
using backend.Models;
using backend.Scoring;
using backend.Services;
using Xunit;

namespace backend.tests;

public sealed class NhlStakesEvidenceTests
{
    internal static JsonElement Capture => ProHypeTests.Fixture("nhl-stakes-401891819");
    internal static DateTime Now => Capture.GetProperty("scoringTime").GetDateTime().ToUniversalTime();
    private static ProQualification Q(double probability) => new(probability, false, Now, Now.Add(PlayoffRatingPolicy.maxAge), "kalshi", ProQualificationEvidenceKind.Kalshi);
    internal static Game SyntheticGame()
    {
        var game = ProHypeTests.GameFromEvent(Capture.GetProperty("board").GetProperty("body").GetProperty("events")[0], "nhl");
        game.HomeTeam = "New York Rangers";
        game.AwayTeam = "Tampa Bay Lightning";
        return game;
    }

    internal static ProCompetitionEvidenceService.Schedule[] Schedules() => Capture.GetProperty("schedules").EnumerateArray().Select(c =>
    {
        var body = c.GetProperty("body");
        return ProCompetitionEvidenceService.ParseSchedule(body, "nhl", 2027, 2,
            body.GetProperty("team").GetProperty("abbreviation").GetString()!, Now)!;
    }).ToArray();

    private static ProGameEvidence Evidence(Game game, ProCompetitionEvidenceService.Schedule[] schedules) =>
        ProCompetitionEvidenceService.ParseEvent(game, Capture.GetProperty("board").GetProperty("body").GetProperty("events")[0],
            schedules, Now, "Synthetic ESPN 2026-10-01");

    [Theory]
    [InlineData("different-start")]
    [InlineData("already-completed")]
    [InlineData("conflicting-completed-count")]
    public void Dominance_cannot_use_progress_from_contradictory_schedule_observations(string problem)
    {
        var game = SyntheticGame();
        var captures = JsonNode.Parse(Capture.GetRawText())!;
        var schedules = captures["schedules"]!.AsArray();
        var home = schedules.Single(s => s!["body"]!["team"]!["abbreviation"]!.GetValue<string>() == "NYR")!["body"]!;
        var ownGame = home["events"]!.AsArray().Single(e => e!["id"]!.GetValue<string>() == game.Id)!;
        if (problem == "different-start") ownGame["date"] = game.ScheduledStart.AddDays(1).ToString("O");
        else if (problem == "already-completed")
        {
            foreach (var s in schedules)
            foreach (var e in s!["body"]!["events"]!.AsArray().Where(e => e!["id"]!.GetValue<string>() == game.Id))
            {
                e!["date"] = Now.AddHours(-2).ToString("O");
                var c = e["competitions"]![0]!;
                c["status"] = new JsonObject { ["period"] = 3, ["type"] = new JsonObject { ["name"] = "STATUS_FINAL", ["state"] = "post", ["completed"] = true } };
                c["competitors"]![0]!["score"] = new JsonObject { ["value"] = 2 };
                c["competitors"]![1]!["score"] = new JsonObject { ["value"] = 1 };
            }
        }
        else
        {
            var final = home["events"]!.AsArray().Single(e => e!["competitions"]![0]!["status"]!["type"]!["completed"]!.GetValue<bool>())!;
            foreach (var s in schedules.Where(s => s!["body"]!["team"]!["abbreviation"]!.GetValue<string>() != "NYR"))
            foreach (var e in s!["body"]!["events"]!.AsArray().Where(e => e!["id"]!.GetValue<string>() == final["id"]!.GetValue<string>()))
                e!["competitions"]![0]!["status"]!["type"] = new JsonObject { ["name"] = "STATUS_SUSPENDED", ["state"] = "in", ["completed"] = false };
        }
        var parsed = schedules.Select(s =>
        {
            var body = JsonSerializer.SerializeToElement(s!["body"]);
            return ProCompetitionEvidenceService.ParseSchedule(body, "nhl", 2027, 2, body.GetProperty("team").GetProperty("abbreviation").GetString()!, Now)!;
        }).ToArray();
        Assert.All(parsed, s => Assert.NotNull(s));
        var evidence = Evidence(game, parsed);
        var result = ProHypePolicy.Calculate(game, evidence, [], Q(.35), Q(.8), null, [], Now);
        Assert.Null(result.Stakes.Value);
        Assert.Null(evidence.HomeProgress.Completed);
    }

    [Fact]
    public void Partial_league_does_not_invent_seeding_but_both_complete_team_schedules_can_bound_stakes()
    {
        var game = SyntheticGame();
        var pair = Schedules().Where(s => s.Team is "NYR" or "TB").ToArray();
        var evidence = Evidence(game, pair);
        var result = ProHypePolicy.Calculate(game, evidence, [], Q(.35), Q(.8), null, [], Now);
        Assert.Equal(5.89172483871624, result.Stakes.Value!.Value, 12);
        Assert.Null(evidence.HomeProgress.SeedingUnresolved);
        evidence = Evidence(game, pair.Where(s => s.Team == "NYR").ToArray());
        Assert.Null(ProHypePolicy.Calculate(game, evidence, [], Q(.35), Q(.8), null, [], Now).Stakes.Value);
    }

    [Fact]
    public void Swapping_home_and_away_in_the_captured_contract_preserves_stakes()
    {
        JsonElement SwapRoles(JsonElement ev)
        {
            var node = JsonNode.Parse(ev.GetRawText())!;
            foreach (var team in node["competitions"]![0]!["competitors"]!.AsArray())
                team!["homeAway"] = team["homeAway"]!.GetValue<string>() == "home" ? "away" : "home";
            return JsonSerializer.SerializeToElement(node);
        }
        var ev = SwapRoles(Capture.GetProperty("board").GetProperty("body").GetProperty("events")[0]);
        var game = ProHypeTests.GameFromEvent(ev, "nhl");
        var schedules = Schedules().Select(s => s with
        {
            Events = s.Events.Select(e => e.GetProperty("id").GetString() == game.Id ? SwapRoles(e) : e).ToArray()
        }).ToArray();
        var evidence = ProCompetitionEvidenceService.ParseEvent(game, ev, schedules, Now, "Synthetic home/away swap of synthetic ESPN-format contracts");
        Assert.True(evidence.VerifiedUnstarted);
        Assert.True(evidence.HomeProgress.ScheduleVerified);
        Assert.True(evidence.AwayProgress.ScheduleVerified);
        Assert.Equal(0, evidence.HomeProgress.Completed);
        Assert.Equal(1, evidence.AwayProgress.Completed);
        var result = ProHypePolicy.Calculate(game, evidence, [], Q(.8), Q(.35), null, [], Now);
        Assert.Equal(5.89172483871624, result.Stakes.Value!.Value, 12);
    }

    [Fact]
    public void Synthetic_Rangers_Lightning_stakes_are_exact_despite_unknown_seeding()
    {
        var game = SyntheticGame();
        var schedules = Schedules();
        Assert.Equal(32, schedules.Length);
        Assert.All(schedules, s => { Assert.NotNull(s); Assert.Equal(84, s.Length); });
        var evidence = ProCompetitionEvidenceService.ParseEvent(game,
            Capture.GetProperty("board").GetProperty("body").GetProperty("events")[0], schedules, Now, "Synthetic ESPN 2026-10-01");
        Assert.True(evidence.VerifiedUnstarted);
        Assert.Equal(1, evidence.HomeProgress.Completed);
        Assert.Equal(0, evidence.AwayProgress.Completed);
        Assert.Null(evidence.HomeProgress.SeedingUnresolved);
        Assert.Null(evidence.AwayProgress.SeedingUnresolved);
        var result = ProHypePolicy.Calculate(game, evidence, [], Q(.35), Q(.8), null, [], Now);
        Assert.True(result.Stakes.Value.HasValue, result.Stakes.Explanation);
        Assert.Equal(5.89172483871624, result.Stakes.Value.Value, 12);
        Assert.Null(result.Quality.Value);  
        Assert.Null(result.Total);
    }
}
