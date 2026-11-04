using System.Text.Json;
using System.Text.Json.Nodes;
using backend.Models;
using backend.Services;
using Xunit;

namespace backend.tests;

public sealed class NhlSeedingWitnessTests
{
    private static JsonElement Change(NhlSeedingScenario scenario, NhlSeedingScenario.Contest contest, Action<JsonObject> change)
    {
        var value = JsonNode.Parse(scenario.Event(contest).GetRawText())!.AsObject();
        change(value);
        return JsonSerializer.SerializeToElement(value);
    }

    private static void Final(JsonObject e, bool homeWins = true, int period = 3)
    {
        e["date"] = JsonValue.Create(NhlSeedingScenario.Now.AddHours(-2));
        var c = e["competitions"]![0]!;
        c["date"] = e["date"]!.DeepClone();
        c["status"] = JsonSerializer.SerializeToNode(new { period, type = new { name = "STATUS_FINAL", state = "post", completed = true } });
        c["competitors"]![0]!["score"] = homeWins ? "3" : "1";
        c["competitors"]![1]!["score"] = homeWins ? "1" : "3";
    }

    [Fact]
    public void Verified_early_season_results_can_be_completed_without_maximizing_every_other_team()
    {
        var scenario = new NhlSeedingScenario();
        var first = scenario.Contests.GroupBy(c => (c.Home, c.Away)).ToDictionary(g => g.Key, g => g.First().Id);
        var schedules = scenario.Schedules(c =>
        {
            var distance = Math.Abs(Array.IndexOf(scenario.Teams, c.Home) - Array.IndexOf(scenario.Teams, c.Away));
            var completed = Math.Min(distance, 32 - distance) <= 7 && first[(c.Home, c.Away)] == c.Id;
            return completed ? Change(scenario, c, e => Final(e)) : scenario.Event(c);
        });
        Assert.Equal(32, schedules.Length);
        Assert.All(schedules, s => Assert.Equal(14, s.Completed));
        Assert.Equal(28, schedules.Single(s => s.Team == "ANA").Points);
        Assert.Equal(14, schedules.Single(s => s.Team == "LA").Points);
        var evidence = scenario.Evidence(schedules);
        Assert.True(evidence.HomeProgress.SeedingUnresolved);
        Assert.True(evidence.AwayProgress.SeedingUnresolved);
    }

    [Theory]
    [InlineData("missing_team")]
    [InlineData("duplicate_team")]
    [InlineData("missing_event")]
    [InlineData("duplicate_event")]
    [InlineData("wrong_season")]
    [InlineData("wrong_phase")]
    [InlineData("wrong_length")]
    [InlineData("conflicting_copy")]
    [InlineData("conflicting_role")]
    [InlineData("in_progress")]
    [InlineData("overtime_in_progress")]
    [InlineData("suspended")]
    [InlineData("postponed")]
    [InlineData("future_score")]
    [InlineData("missing_date")]
    [InlineData("past_scheduled")]
    [InlineData("unknown_team")]
    [InlineData("missing_role")]
    [InlineData("target_role")]
    [InlineData("record_mismatch")]
    public void A_possible_completion_never_substitutes_for_rejected_or_unverified_source_facts(string fault)
    {
        var scenario = new NhlSeedingScenario();
        var schedules = scenario.Schedules();
        var outsider = scenario.Contests.First(c => c.Home == "BOS" && c.Away == "BUF");
        JsonElement Mutate(NhlSeedingScenario.Contest contest) => Change(scenario, contest, e =>
        {
            var c = e["competitions"]![0]!;
            switch (fault)
            {
                case "conflicting_copy": e["date"] = JsonValue.Create(scenario.Game.ScheduledStart.AddHours(1)); break;
                case "conflicting_role":
                case "target_role": c["competitors"]![0]!["homeAway"] = "away"; c["competitors"]![1]!["homeAway"] = "home"; break;
                case "in_progress": c["status"] = JsonSerializer.SerializeToNode(new { period = 1, type = new { name = "STATUS_IN_PROGRESS", state = "in", completed = false } }); break;
                case "overtime_in_progress": c["status"] = JsonSerializer.SerializeToNode(new { period = 4, type = new { name = "STATUS_IN_PROGRESS", state = "in", completed = false } }); break;
                case "suspended": c["status"]!["type"]!["name"] = "STATUS_SUSPENDED"; break;
                case "postponed": c["status"]!["type"]!["name"] = "STATUS_POSTPONED"; break;
                case "future_score": c["competitors"]![0]!["score"] = "1"; break;
                case "missing_date": e.Remove("date"); break;
                case "past_scheduled": e["date"] = JsonValue.Create(NhlSeedingScenario.Now.AddMinutes(-1)); break;
                case "unknown_team": c["competitors"]![0]!["team"]!["abbreviation"] = "UNKNOWN"; break;
                case "missing_role": c["competitors"]![0]!.AsObject().Remove("homeAway"); break;
            }
        });
        switch (fault)
        {
            case "missing_team": schedules = schedules.Skip(1).ToArray(); break;
            case "duplicate_team": schedules = schedules.Skip(1).Append(schedules[1]).ToArray(); break;
            case "missing_event": schedules[1] = schedules[1] with { Events = schedules[1].Events.Skip(1).ToArray() }; break;
            case "duplicate_event": schedules[1] = schedules[1] with { Events = schedules[1].Events.Skip(1).Append(schedules[1].Events[1]).ToArray() }; break;
            case "wrong_season": schedules[1] = schedules[1] with { Season = 2026 }; break;
            case "wrong_phase": schedules[1] = schedules[1] with { Phase = 3 }; break;
            case "wrong_length": schedules[1] = schedules[1] with { Length = 83 }; break;
            case "record_mismatch": schedules[1] = schedules[1] with { Points = 1 }; break;
            default:
                var changed = fault == "target_role" ? scenario.Target : outsider;
                for (var i = 0; i < schedules.Length; i++)
                {
                    if (fault is "conflicting_copy" or "conflicting_role" && schedules[i].Team != changed.Home) continue;
                    schedules[i] = schedules[i] with { Events = schedules[i].Events.Select(e => e.GetProperty("id").GetString() == changed.Id ? Mutate(changed) : e).ToArray() };
                }
                break;
        }
        var evidence = scenario.Evidence(schedules);
        Assert.NotEqual(true, evidence.HomeProgress.SeedingUnresolved);
        Assert.NotEqual(true, evidence.AwayProgress.SeedingUnresolved);
    }

    [Fact]
    public void Overtime_loss_points_and_completed_ids_are_preserved_in_the_completion()
    {
        var scenario = new NhlSeedingScenario();
        var completed = scenario.Contests.First(c => c.Home == "BOS" && c.Away == "BUF").Id;
        var schedules = scenario.Schedules(c => c.Id == completed ? Change(scenario, c, e => Final(e, period: 4)) : scenario.Event(c));
        Assert.Equal(2, schedules.Single(s => s.Team == "BOS").Points);
        Assert.Equal(1, schedules.Single(s => s.Team == "BUF").Points);
        var evidence = scenario.Evidence(schedules);
        Assert.Contains(completed, evidence.CompletedGameIds);
        Assert.True(evidence.HomeProgress.SeedingUnresolved);
        Assert.True(evidence.AwayProgress.SeedingUnresolved);
    }

    [Fact]
    public void A_tied_result_is_not_a_strict_position_exchange()
    {
        var scenario = new NhlSeedingScenario();
        var schedules = scenario.Schedules(c => c.Id == scenario.Target.Id ? scenario.Event(c) : Change(scenario, c, e =>
            Final(e, homeWins: c.Home == "ANA" || c.Home == "LA" || c.Away != "ANA" && c.Away != "LA")));
        Assert.Equal(166, schedules.Single(s => s.Team == "ANA").Points);
        Assert.Equal(164, schedules.Single(s => s.Team == "LA").Points);
        var evidence = scenario.Evidence(schedules);
        Assert.NotEqual(true, evidence.HomeProgress.SeedingUnresolved);
        Assert.NotEqual(true, evidence.AwayProgress.SeedingUnresolved);
    }

    [Fact]
    public void Target_teams_that_cannot_both_qualify_do_not_get_a_seeding_certificate()
    {
        var scenario = new NhlSeedingScenario();
        var schedules = scenario.Schedules(c =>
        {
            var distance = Math.Abs(Array.IndexOf(scenario.Teams, c.Home) - Array.IndexOf(scenario.Teams, c.Away));
            return Math.Min(distance, 32 - distance) == 12 ? scenario.Event(c)
                : Change(scenario, c, e => Final(e, homeWins: c.Home is not ("ANA" or "LA")));
        });
        Assert.All(schedules, s => Assert.Equal(80, s.Completed));
        Assert.Equal(0, schedules.Single(s => s.Team == "ANA").Points);
        Assert.Equal(0, schedules.Single(s => s.Team == "LA").Points);
        var evidence = scenario.Evidence(schedules);
        Assert.NotEqual(true, evidence.HomeProgress.SeedingUnresolved);
        Assert.NotEqual(true, evidence.AwayProgress.SeedingUnresolved);
    }

    [Fact]
    public void Proof_is_stable_under_input_order_and_canonical_Utah_alias()
    {
        var scenario = new NhlSeedingScenario();
        var schedules = scenario.Schedules(c => Change(scenario, c, e =>
        {
            foreach (var member in e["competitions"]![0]!["competitors"]!.AsArray())
                if (member!["team"]!["abbreviation"]!.GetValue<string>() == "UTAH") member["team"]!["abbreviation"] = "UTA";
        }));
        var evidence = scenario.Evidence(schedules.Reverse().Select(s => s with { Events = s.Events.Reverse().ToArray() }).ToArray());
        Assert.True(evidence.HomeProgress.SeedingUnresolved);
        Assert.True(evidence.AwayProgress.SeedingUnresolved);
    }

    [Fact]
    public void Two_meeting_exchange_with_a_legal_non_head_to_head_loss_is_certified()
    {
        var scenario = new NhlSeedingScenario();
        var schedules = scenario.Schedules();
        Assert.Equal(1344, scenario.Contests.Length);
        Assert.Equal(32, schedules.Length);
        Assert.All(schedules, s => { Assert.NotNull(s); Assert.Equal(84, s.Events.Length); Assert.Equal(0, s.Points); });
        var homeWins = scenario.IndependentCompletion("ANA");
        var awayWins = scenario.IndependentCompletion("LA");
        Assert.Equal((166, 164), (homeWins["ANA"], homeWins["LA"]));
        Assert.Equal((164, 166), (awayWins["ANA"], awayWins["LA"]));
        Assert.Equal(2688, homeWins.Values.Sum());
        Assert.Equal(2688, awayWins.Values.Sum());
        Assert.Equal(158, homeWins.Where(p => p.Key is not ("ANA" or "LA")).Max(p => p.Value));
        Assert.All(homeWins.Keys.Where(team => team is not ("ANA" or "LA")), team => Assert.Equal(homeWins[team], awayWins[team]));
        var evidence = scenario.Evidence(schedules);
        Assert.True(evidence.VerifiedUnstarted);
        Assert.True(evidence.HomeProgress.ScheduleVerified);
        Assert.True(evidence.AwayProgress.ScheduleVerified);
        Assert.True(evidence.HomeProgress.SeedingUnresolved);
        Assert.True(evidence.AwayProgress.SeedingUnresolved);
    }
}
