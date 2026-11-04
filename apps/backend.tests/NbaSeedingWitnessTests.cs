using System.Text.Json;
using System.Text.Json.Nodes;
using backend.Services;
using Xunit;

namespace backend.tests;

public sealed class NbaSeedingWitnessTests
{
    [Fact]
    public void Provisional_NBA_schedules_can_prove_a_strict_exchange_without_assigning_unpublished_matchups()
    {
        var scenario = new NbaSeedingScenario();
        var schedules = scenario.Schedules();
        Assert.Equal(1200, scenario.Contests.Length);
        Assert.Equal(30, schedules.Length);
        Assert.All(schedules, s => { Assert.Equal(80, s.Events.Length); Assert.Equal(82, s.Length); });
        var evidence = scenario.Evidence(schedules);
        Assert.True(evidence.HomeProgress.ScheduleVerified);
        Assert.True(evidence.AwayProgress.ScheduleVerified);
        Assert.True(evidence.HomeProgress.SeedingUnresolved);
        Assert.True(evidence.AwayProgress.SeedingUnresolved);
        Assert.Contains("unpublished", evidence.HomeProgress.Reason);
        for (var homeExtra = 0; homeExtra <= 2; homeExtra++)
        for (var awayExtra = 0; awayExtra <= 2; awayExtra++)
        {
            var homeWins = scenario.IndependentCompletion(homeExtra, awayExtra, "BOS");
            var awayWins = scenario.IndependentCompletion(homeExtra, awayExtra, "MIA");
            Assert.Equal(homeWins["BOS"], homeWins["MIA"] + 1);
            Assert.Equal(awayWins["MIA"], awayWins["BOS"] + 1);
            Assert.Equal(homeWins["BOS"] - 1, awayWins["BOS"]);
            Assert.Equal(homeWins["MIA"] + 1, awayWins["MIA"]);
            Assert.Equal(1200 + homeExtra + awayExtra, homeWins.Values.Sum());
            Assert.All(scenario.Teams.Where(t => t is not ("BOS" or "MIA")), team =>
            {
                Assert.Equal(homeWins[team], awayWins[team]);
                Assert.True(homeWins[team] + 2 < homeWins["MIA"]);
            });
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Actually_published_additional_games_reduce_unknown_capacity_without_changing_the_denominator(int published)
    {
        var scenario = new NbaSeedingScenario();
        var schedules = scenario.Teams.Select(team =>
        {
            var body = JsonNode.Parse(scenario.ScheduleJson(team).GetRawText())!;
            for (var round = 0; round < published; round++)
            for (var index = 0; index < 15; index++)
            {
                var home = scenario.Teams[index]; var away = scenario.Teams[index + 15];
                if (team == home || team == away)
                    body["events"]!.AsArray().Add(JsonNode.Parse(scenario.Event(new($"synthetic-published-{round}-{index}", home, away)).GetRawText()));
            }
            return ProCompetitionEvidenceService.ParseSchedule(JsonSerializer.SerializeToElement(body), "nba", 2027, 2, team, NbaSeedingScenario.Now);
        }).OfType<ProCompetitionEvidenceService.Schedule>().ToArray();
        Assert.Equal(30, schedules.Length);
        Assert.All(schedules, s => Assert.Equal(80 + published, s.Events.Length));
        var evidence = scenario.Evidence(schedules.Reverse().ToArray());
        Assert.True(evidence.HomeProgress.SeedingUnresolved);
        Assert.True(evidence.AwayProgress.SeedingUnresolved);
    }

    [Fact]
    public void Current_completed_records_are_preserved_without_requiring_both_targets_to_win_every_other_game()
    {
        var scenario = new NbaSeedingScenario();
        var schedules = scenario.Schedules(scenario.EarlyEvent);
        Assert.Equal(30, schedules.Length);
        Assert.Equal((7, 3), (schedules.Single(s => s.Team == "BOS").Completed, schedules.Single(s => s.Team == "BOS").Points));
        Assert.Equal((8, 4), (schedules.Single(s => s.Team == "MIA").Completed, schedules.Single(s => s.Team == "MIA").Points));
        var evidence = scenario.Evidence(schedules);
        Assert.True(evidence.HomeProgress.SeedingUnresolved);
        Assert.True(evidence.AwayProgress.SeedingUnresolved);
    }

    [Fact]
    public void Overtime_counts_as_one_observed_win_and_a_tie_never_becomes_a_result()
    {
        var scenario = new NbaSeedingScenario();
        var completed = scenario.Contests.First(c => c.Home == "ATL" && c.Away == "BKN");
        JsonElement Result(bool tie) => scenario.Change(completed, e =>
        {
            e["date"] = JsonValue.Create(NbaSeedingScenario.Now.AddDays(-1));
            var c = e["competitions"]![0]!;
            c["status"] = JsonSerializer.SerializeToNode(new { period = 5, type = new { name = "STATUS_FINAL_OVERTIME", state = "post", completed = true } });
            c["competitors"]![0]!["score"] = "125.00"; c["competitors"]![1]!["score"] = tie ? "125.0" : "124.0";
        });
        var schedules = scenario.Schedules(c => c.Id == completed.Id ? Result(false) : scenario.Event(c));
        Assert.Equal(1, schedules.Single(s => s.Team == "ATL").Points);
        Assert.Equal(0, schedules.Single(s => s.Team == "BKN").Points);
        var evidence = scenario.Evidence(schedules);
        Assert.Contains(completed.Id, evidence.CompletedGameIds);
        Assert.True(evidence.HomeProgress.SeedingUnresolved);
        Assert.True(evidence.AwayProgress.SeedingUnresolved);
        schedules = scenario.Schedules(c => c.Id == completed.Id ? Result(true) : scenario.Event(c));
        Assert.Equal(28, schedules.Length);
        Assert.Null(scenario.Evidence(schedules).HomeProgress.SeedingUnresolved);
    }

    [Fact]
    public void A_hockey_shootout_label_cannot_establish_a_completed_basketball_result()
    {
        var scenario = new NbaSeedingScenario();
        var completed = scenario.Contests.First(c => c.Home == "ATL" && c.Away == "BKN");
        var schedules = scenario.Schedules(c => c.Id != completed.Id ? scenario.Event(c) : scenario.Change(c, e =>
        {
            e["date"] = JsonValue.Create(NbaSeedingScenario.Now.AddDays(-1));
            var value = e["competitions"]![0]!;
            value["status"] = JsonSerializer.SerializeToNode(new { period = 5, type = new { name = "STATUS_FINAL_SO", state = "post", completed = true } });
            value["competitors"]![0]!["score"] = "110"; value["competitors"]![1]!["score"] = "100";
        }));
        Assert.Equal(30, schedules.Length);
        var evidence = scenario.Evidence(schedules);
        Assert.Null(evidence.HomeProgress.SeedingUnresolved);
        Assert.Null(evidence.AwayProgress.SeedingUnresolved);
    }

    [Fact]
    public void Low_record_teams_that_cannot_both_qualify_do_not_receive_a_certificate()
    {
        var scenario = new NbaSeedingScenario();
        var schedules = scenario.Schedules(c =>
        {
            var distance = Math.Abs(Array.IndexOf(scenario.Teams, c.Home) - Array.IndexOf(scenario.Teams, c.Away));
            if (Math.Min(distance, 30 - distance) == 14) return scenario.Event(c);
            return scenario.Change(c, e =>
            {
                e["date"] = JsonValue.Create(NbaSeedingScenario.Now.AddDays(-1));
                var value = e["competitions"]![0]!;
                value["status"] = JsonSerializer.SerializeToNode(new { period = 4, type = new { name = "STATUS_FINAL", state = "post", completed = true } });
                var homeWins = c.Home is not ("BOS" or "MIA");
                value["competitors"]![0]!["score"] = homeWins ? "110" : "100";
                value["competitors"]![1]!["score"] = homeWins ? "100" : "110";
            });
        });
        Assert.Equal(30, schedules.Length);
        Assert.All(schedules, s => Assert.Equal(76, s.Completed));
        Assert.Equal(0, schedules.Single(s => s.Team == "BOS").Points);
        Assert.Equal(0, schedules.Single(s => s.Team == "MIA").Points);
        var evidence = scenario.Evidence(schedules);
        Assert.Null(evidence.HomeProgress.SeedingUnresolved);
        Assert.Null(evidence.AwayProgress.SeedingUnresolved);
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
    [InlineData("conflicting_cup_label")]
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
    [InlineData("completed_ids")]
    [InlineData("event_season")]
    [InlineData("event_phase")]
    public void Unknown_matchups_never_excuse_unsafe_observed_facts(string fault)
    {
        var scenario = new NbaSeedingScenario();
        var schedules = scenario.Schedules();
        var outsider = scenario.Contests.First(c => c.Home == "ATL" && c.Away == "BKN");
        JsonElement Mutate(NbaSeedingScenario.Contest contest) => scenario.Change(contest, e =>
        {
            var c = e["competitions"]![0]!;
            switch (fault)
            {
                case "conflicting_copy": e["date"] = JsonValue.Create(NbaSeedingScenario.Now.AddDays(1)); break;
                case "conflicting_role":
                case "target_role": c["competitors"]![0]!["homeAway"] = "away"; c["competitors"]![1]!["homeAway"] = "home"; break;
                case "conflicting_cup_label": c["notes"] = JsonSerializer.SerializeToNode(new[] { new { headline = "NBA Cup - Group Play" } }); break;
                case "in_progress": c["status"] = JsonSerializer.SerializeToNode(new { period = 1, type = new { name = "STATUS_IN_PROGRESS", state = "in", completed = false } }); break;
                case "overtime_in_progress": c["status"] = JsonSerializer.SerializeToNode(new { period = 5, type = new { name = "STATUS_IN_PROGRESS", state = "in", completed = false } }); break;
                case "suspended": c["status"]!["type"]!["name"] = "STATUS_SUSPENDED"; break;
                case "postponed": c["status"]!["type"]!["name"] = "STATUS_POSTPONED"; break;
                case "future_score": c["competitors"]![0]!["score"] = "1"; break;
                case "missing_date": e.Remove("date"); break;
                case "past_scheduled": e["date"] = JsonValue.Create(NbaSeedingScenario.Now.AddMinutes(-1)); break;
                case "unknown_team": c["competitors"]![0]!["team"]!["abbreviation"] = "UNKNOWN"; break;
                case "missing_role": c["competitors"]![0]!.AsObject().Remove("homeAway"); break;
                case "event_season": e["season"]!["year"] = 2026; break;
                case "event_phase": e["seasonType"]!["type"] = 3; break;
            }
        });
        switch (fault)
        {
            case "missing_team": schedules = schedules.Skip(1).ToArray(); break;
            case "duplicate_team": schedules = schedules.Skip(1).Append(schedules[1]).ToArray(); break;
            case "missing_event": schedules[0] = schedules[0] with { Events = schedules[0].Events.Skip(1).ToArray() }; break;
            case "duplicate_event": schedules[0] = schedules[0] with { Events = schedules[0].Events.Skip(1).Append(schedules[0].Events[1]).ToArray() }; break;
            case "wrong_season": schedules[0] = schedules[0] with { Season = 2026 }; break;
            case "wrong_phase": schedules[0] = schedules[0] with { Phase = 3 }; break;
            case "wrong_length": schedules[0] = schedules[0] with { Length = 81 }; break;
            case "record_mismatch": schedules[0] = schedules[0] with { Points = 1 }; break;
            case "completed_ids": schedules[0] = schedules[0] with { CompletedIds = ["unobserved-result"] }; break;
            default:
                var changed = fault == "target_role" ? scenario.Target : outsider;
                for (var i = 0; i < schedules.Length; i++)
                {
                    if (fault is "conflicting_copy" or "conflicting_role" or "conflicting_cup_label" && schedules[i].Team != changed.Home) continue;
                    schedules[i] = schedules[i] with { Events = schedules[i].Events.Select(e => e.GetProperty("id").GetString() == changed.Id ? Mutate(changed) : e).ToArray() };
                }
                break;
        }
        var evidence = scenario.Evidence(schedules);
        Assert.NotEqual(true, evidence.HomeProgress.SeedingUnresolved);
        Assert.NotEqual(true, evidence.AwayProgress.SeedingUnresolved);
    }

    [Theory]
    [InlineData("cup")]
    [InlineData("missing_notes")]
    [InlineData("missing_type")]
    [InlineData("unknown_type")]
    public void The_target_must_be_explicitly_ordinary_not_an_ambiguous_or_Cup_game(string fault)
    {
        var scenario = new NbaSeedingScenario();
        JsonElement Target() => scenario.Change(scenario.Target, e =>
        {
            var c = e["competitions"]![0]!.AsObject();
            if (fault == "cup") c["notes"] = JsonSerializer.SerializeToNode(new[] { new { headline = "NBA Cup - Group Play" } });
            else if (fault == "missing_notes") c.Remove("notes");
            else if (fault == "missing_type") c.Remove("type");
            else c["type"]!["abbreviation"] = "UNKNOWN";
        });
        var evidence = scenario.Evidence(scenario.Schedules(c => c.Id == scenario.Target.Id ? Target() : scenario.Event(c)), Target());
        Assert.Null(evidence.HomeProgress.SeedingUnresolved);
        Assert.Null(evidence.AwayProgress.SeedingUnresolved);
    }

    [Theory]
    [InlineData("cup")]
    [InlineData("missing_notes")]
    [InlineData("missing_type")]
    public void Scoreboard_classification_must_independently_support_the_ordinary_target(string fault)
    {
        var scenario = new NbaSeedingScenario();
        var target = scenario.Change(scenario.Target, e =>
        {
            var c = e["competitions"]![0]!.AsObject();
            if (fault == "cup") c["notes"] = JsonSerializer.SerializeToNode(new[] { new { headline = "NBA Cup - Group Play" } });
            else if (fault == "missing_notes") c.Remove("notes");
            else c.Remove("type");
        });
        var evidence = scenario.Evidence(target: target);
        Assert.True(evidence.HomeProgress.ScheduleVerified && evidence.AwayProgress.ScheduleVerified);
        Assert.Null(evidence.HomeProgress.SeedingUnresolved);
        Assert.Null(evidence.AwayProgress.SeedingUnresolved);
    }

    [Theory]
    [InlineData("2026-11-21T00:30:00Z")]
    [InlineData("2026-11-25T00:30:00Z")]
    [InlineData("2026-11-26T00:30:00Z")]
    [InlineData("2026-12-09T00:30:00Z")]
    public void Official_Eastern_Cup_dates_cannot_be_misclassified_by_an_empty_provider_label(string start)
    {
        var scenario = new NbaSeedingScenario();
        var game = scenario.Game;
        game.ScheduledStart = DateTimeOffset.Parse(start, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime;
        JsonElement Target() => scenario.Change(scenario.Target, e => e["date"] = JsonValue.Create(game.ScheduledStart));
        var schedules = scenario.Schedules(c => c.Id == scenario.Target.Id ? Target() : scenario.Event(c));
        var evidence = ProCompetitionEvidenceService.ParseEvent(game, Target(), schedules, NbaSeedingScenario.Now, "synthetic source");
        Assert.True(evidence.VerifiedUnstarted);
        Assert.Null(evidence.HomeProgress.SeedingUnresolved);
        Assert.Null(evidence.AwayProgress.SeedingUnresolved);
    }

    [Fact]
    public void Fixed_known_Cup_group_results_do_not_prevent_balancing_later_ordinary_games()
    {
        var scenario = new NbaSeedingScenario();
        var cup = scenario.Contests.First(c => c.Home == "ATL" && c.Away == "BOS");
        var schedules = scenario.Schedules(c => c.Id != cup.Id ? scenario.Event(c) : scenario.Change(c, e =>
        {
            e["date"] = JsonValue.Create(new DateTime(2026, 11, 21, 0, 30, 0, DateTimeKind.Utc));
            e["competitions"]![0]!["notes"] = JsonSerializer.SerializeToNode(new[] { new { headline = "NBA Cup - Group Play" } });
        }));
        var evidence = scenario.Evidence(schedules);
        Assert.True(evidence.HomeProgress.SeedingUnresolved);
        Assert.True(evidence.AwayProgress.SeedingUnresolved);
    }

    [Theory]
    [InlineData("NBA Cup - Championship")]
    [InlineData("NBA Cup - Unsupported Stage")]
    public void Noncounting_or_unrecognized_Cup_games_cannot_reduce_the_unknown_regular_season_capacity(string label)
    {
        var scenario = new NbaSeedingScenario();
        var cup = scenario.Contests.First(c => c.Home == "ATL" && c.Away == "BKN");
        var schedules = scenario.Schedules(c => c.Id != cup.Id ? scenario.Event(c) : scenario.Change(c, e =>
            e["competitions"]![0]!["notes"] = JsonSerializer.SerializeToNode(new[] { new { headline = label } })));
        var evidence = scenario.Evidence(schedules);
        Assert.Null(evidence.HomeProgress.SeedingUnresolved);
        Assert.Null(evidence.AwayProgress.SeedingUnresolved);
    }

    [Fact]
    public void Cup_outcomes_cannot_be_used_as_the_free_balancing_losses()
    {
        var scenario = new NbaSeedingScenario();
        var schedules = scenario.Schedules(c => c.Id == scenario.Target.Id ? scenario.Event(c) : scenario.Change(c, e =>
            e["competitions"]![0]!["notes"] = JsonSerializer.SerializeToNode(new[] { new { headline = "NBA Cup - Group Play" } })));
        var evidence = scenario.Evidence(schedules);
        Assert.Null(evidence.HomeProgress.SeedingUnresolved);
        Assert.Null(evidence.AwayProgress.SeedingUnresolved);
    }

    [Fact]
    public void Some_feasible_missing_win_counts_do_not_substitute_for_proving_every_case()
    {
        var scenario = new NbaSeedingScenario();
        var permitted = new[] { "BOS", "MIA" }.Select(team => scenario.Contests.First(c => c.Home == "ATL" && c.Away == team).Id).ToHashSet();
        var schedules = scenario.Schedules(c => c.Id == scenario.Target.Id || permitted.Contains(c.Id) ? scenario.Event(c)
            : scenario.Change(c, e => e["date"] = JsonValue.Create(new DateTime(2026, 11, 17, 20, 0, 0, DateTimeKind.Utc))));
        Assert.Equal(30, schedules.Length);
        var evidence = scenario.Evidence(schedules);
        Assert.Null(evidence.HomeProgress.SeedingUnresolved);
        Assert.Null(evidence.AwayProgress.SeedingUnresolved);
    }
}
