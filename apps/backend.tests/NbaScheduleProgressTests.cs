using System.Text.Json;
using System.Text.Json.Nodes;
using backend.Models;
using backend.Scoring;
using backend.Services;
using Xunit;

namespace backend.tests;

public sealed class NbaScheduleProgressTests
{
    internal static readonly DateTime Now = new(2026, 11, 16, 15, 0, 0, DateTimeKind.Utc);
    internal static Game Game() => new()
    {
        Id = "synthetic-nba-progress", Sport = "nba", SeasonYear = 2027, SeasonType = 2,
        HomeTeam = "Boston Celtics", AwayTeam = "Miami Heat", HomeTeamAbbreviation = "BOS", AwayTeamAbbreviation = "MIA",
        ScheduledStart = Now.AddHours(3), Status = "scheduled"
    };

    internal static JsonObject ScheduleBody(string team, int length = 80, int completed = 6)
    {
        var game = Game();
        var events = new JsonArray();
        for (var i = 0; i < length; i++)
        {
            var final = i < completed;
            var homeWins = i % 3 != 0;
            events.Add(JsonSerializer.SerializeToNode(new
            {
                id = i == completed ? game.Id : $"synthetic-nba-event-{i}",
                date = final ? Now.AddDays(i - completed) : i == completed ? game.ScheduledStart : Now.AddDays(i + 1),
                season = new { year = 2027 }, seasonType = new { type = 2 },
                competitions = new[] { new
                {
                    status = new { period = final ? 4 : 0, type = new { name = final ? "STATUS_FINAL" : "STATUS_SCHEDULED", state = final ? "post" : "pre", completed = final } },
                    competitors = new[]
                    {
                        new { id = "2", homeAway = "home", team = new { abbreviation = "BOS" }, score = new { value = final ? homeWins ? 109.0 : 101.0 : 0.0 } },
                        new { id = "14", homeAway = "away", team = new { abbreviation = "MIA" }, score = new { value = final ? homeWins ? 101.0 : 109.0 : 0.0 } }
                    }
                } }
            }));
        }
        var wins = Enumerable.Range(0, completed).Count(i => team == "BOS" ? i % 3 != 0 : i % 3 == 0);
        return new JsonObject
        {
            ["status"] = "success", ["timestamp"] = Now,
            ["season"] = new JsonObject { ["year"] = 2027, ["type"] = 2 },
            ["team"] = new JsonObject { ["abbreviation"] = team, ["seasonSummary"] = "2026-27", ["recordSummary"] = $"{wins}-{completed - wins}" },
            ["events"] = events
        };
    }

    internal static ProCompetitionEvidenceService.Schedule? Parse(JsonObject body, string team) =>
        ProCompetitionEvidenceService.ParseSchedule(JsonSerializer.SerializeToElement(body), "nba", 2027, 2, team, Now);

    internal static JsonElement Upcoming(JsonObject body) => JsonSerializer.SerializeToElement(new
    {
        id = Game().Id, date = Game().ScheduledStart, season = new { year = 2027, type = 2 },
        competitions = new[] { body["events"]!.AsArray().Single(e => e!["id"]!.GetValue<string>() == Game().Id)!["competitions"]![0]!.DeepClone() }
    });

    [Theory]
    [InlineData(80, 6)]
    [InlineData(81, 0)]
    public void Unpublished_NBA_matchups_do_not_erase_corroborated_completed_progress(int length, int completed)
    {
        var home = ScheduleBody("BOS", length, completed);
        var away = ScheduleBody("MIA", length, completed);
        var h = Parse(home, "BOS"); var a = Parse(away, "MIA");
        Assert.NotNull(h); Assert.NotNull(a);
        Assert.Equal(82, h.Length); Assert.Equal(length, h.Events.Length);
        Assert.Equal(completed, h.Completed); Assert.Equal(completed, a.Completed);
        var context = ProCompetitionEvidenceService.ParseEvent(Game(), Upcoming(home), [h, a], Now, "synthetic ESPN schedule");
        Assert.Equal(ProCompetitionRejection.None, context.Rejection);
        Assert.True(context.VerifiedUnstarted);
        Assert.True(context.HomeProgress.ScheduleVerified); Assert.True(context.AwayProgress.ScheduleVerified);
        Assert.Equal(82, context.HomeProgress.Scheduled); Assert.Equal(completed, context.HomeProgress.Completed);
        Assert.Null(context.HomeProgress.SeedingUnresolved); Assert.Null(context.AwayProgress.SeedingUnresolved);
        var game = Game();
        var qualification = new ProQualification(.5, false, Now, Now.AddHours(1), "kalshi", ProQualificationEvidenceKind.Kalshi);
        var ranks = ProHypeTests.Ranks(game); foreach (var rank in ranks) rank.FetchedAt = Now;
        var odds = ProHypeTests.Odds(game); odds.FetchedAt = Now;
        var result = ProHypePolicy.Calculate(game, context, ranks, qualification, qualification, odds, context.CompletedGameIds, Now);
        Assert.NotNull(result.Total);
        Assert.Contains("dominate all unknown seeding", result.Stakes.Explanation);
        var nearCertain = qualification with { Probability = .99 };
        Assert.Null(ProHypePolicy.Calculate(game, context, ranks, nearCertain, nearCertain, odds, context.CompletedGameIds, Now).Total);
    }

    [Theory]
    [InlineData("missing-record")]
    [InlineData("missing-record-season")]
    [InlineData("wrong-record-season")]
    [InlineData("wrong-wins")]
    [InlineData("wrong-total")]
    [InlineData("malformed-record")]
    [InlineData("wrong-team")]
    [InlineData("wrong-season")]
    [InlineData("wrong-phase")]
    [InlineData("event-season")]
    [InlineData("event-phase")]
    [InlineData("duplicate-event")]
    [InlineData("missing-final")]
    [InlineData("reversed-final")]
    [InlineData("future-final")]
    [InlineData("fractional-score")]
    [InlineData("tiny-fractional-score")]
    [InlineData("nonfinite-score")]
    [InlineData("negative-score")]
    [InlineData("missing-score")]
    [InlineData("unknown-status")]
    [InlineData("canceled")]
    [InlineData("too-short")]
    [InlineData("too-long")]
    [InlineData("stale")]
    [InlineData("future-observation")]
    public void Partial_progress_requires_matching_record_identity_and_all_completed_results(string problem)
    {
        var body = ScheduleBody("BOS");
        var events = body["events"]!.AsArray();
        var final = events[0]!["competitions"]![0]!;
        var upcoming = events[6]!["competitions"]![0]!;
        switch (problem)
        {
            case "missing-record": body["team"]!.AsObject().Remove("recordSummary"); break;
            case "missing-record-season": body["team"]!.AsObject().Remove("seasonSummary"); break;
            case "wrong-record-season": body["team"]!["seasonSummary"] = "2025-26"; break;
            case "wrong-wins": body["team"]!["recordSummary"] = "2-4"; break;
            case "wrong-total": body["team"]!["recordSummary"] = "4-3"; break;
            case "malformed-record": body["team"]!["recordSummary"] = "4-1-1"; break;
            case "wrong-team": body["team"]!["abbreviation"] = "NY"; break;
            case "wrong-season": body["season"]!["year"] = 2026; break;
            case "wrong-phase": body["season"]!["type"] = 3; break;
            case "event-season": events[0]!["season"]!["year"] = 2026; break;
            case "event-phase": events[0]!["seasonType"]!["type"] = 3; break;
            case "duplicate-event": events[1]!["id"] = events[0]!["id"]!.GetValue<string>(); break;
            case "missing-final":
                events[0] = events[^1]!.DeepClone(); events[0]!["id"] = "synthetic-omitted-final"; break;
            case "reversed-final":
                final["competitors"]![0]!["score"]!["value"] = 109;
                final["competitors"]![1]!["score"]!["value"] = 101; break;
            case "future-final": events[0]!["date"] = Now.AddHours(1); break;
            case "fractional-score": final["competitors"]![0]!["score"]!["value"] = 101.5; break;
            case "tiny-fractional-score": final["competitors"]![0]!["score"]!["value"] = "101.00000000000000000000001"; break;
            case "nonfinite-score": final["competitors"]![0]!["score"]!["value"] = "NaN"; break;
            case "negative-score": final["competitors"]![0]!["score"]!["value"] = -1; break;
            case "missing-score": final["competitors"]![0]!.AsObject().Remove("score"); break;
            case "unknown-status": upcoming["status"]!["type"]!["name"] = "STATUS_UNKNOWN"; break;
            case "canceled": upcoming["status"]!["type"]!["name"] = "STATUS_CANCELED"; break;
            case "too-short": events.RemoveAt(events.Count - 1); break;
            case "too-long":
                for (var i = 0; i < 3; i++)
                { var extra = events[^1]!.DeepClone(); extra["id"] = "synthetic-extra-" + i; events.Add(extra); }
                break;
            case "stale": body["timestamp"] = Now.AddHours(-1); break;
            case "future-observation": body["timestamp"] = Now.AddTicks(1); break;
        }
        Assert.Null(Parse(body, "BOS"));
    }

    [Fact]
    public void Decimal_zero_schedule_scores_remain_verified_unstarted()
    {
        var home = ScheduleBody("BOS"); var away = ScheduleBody("MIA");
        foreach (var body in new[] { home, away })
            foreach (var competitor in body["events"]![6]!["competitions"]![0]!["competitors"]!.AsArray())
                competitor!["score"]!["value"] = 0.00m;
        var h = Parse(home, "BOS"); var a = Parse(away, "MIA");
        Assert.NotNull(h); Assert.NotNull(a);
        var context = ProCompetitionEvidenceService.ParseEvent(Game(), Upcoming(home), [h, a], Now, "synthetic ESPN schedule");
        Assert.True(context.VerifiedUnstarted, context.Reason);
        Assert.True(context.HomeProgress.ScheduleVerified); Assert.True(context.AwayProgress.ScheduleVerified);
    }

    [Fact]
    public void Full_NBA_schedules_keep_the_existing_contract_without_a_record_summary()
    {
        var body = ScheduleBody("BOS", 82);
        body["team"]!.AsObject().Remove("recordSummary");
        body["team"]!.AsObject().Remove("seasonSummary");
        var schedule = Parse(body, "BOS");
        Assert.NotNull(schedule); Assert.Equal(82, schedule.Length); Assert.Equal(6, schedule.Completed);
    }

    [Fact]
    public void Provisional_matchups_cannot_supply_full_league_seeding_certificates()
    {
        var all = Enumerable.Range(0, 30).Select(_ => new List<JsonElement>()).ToArray();
        var id = 0;
        for (var h = 0; h < 30; h++)
            for (var a = h + 1; a < 30; a++)
            {
                var distance = Math.Min(a - h, 30 - (a - h));
                for (var meeting = 0; meeting < (distance <= 11 ? 3 : 2); meeting++)
                {
                    var e = JsonSerializer.SerializeToElement(new
                    {
                        id = $"synthetic-pair-{id++}", date = Now.AddDays(1),
                        competitions = new[] { new
                        {
                            status = new { period = 0, type = new { name = "STATUS_SCHEDULED", state = "pre", completed = false } },
                            competitors = new[] { new { team = new { abbreviation = $"T{h}" } }, new { team = new { abbreviation = $"T{a}" } } }
                        } }
                    });
                    all[h].Add(e); all[a].Add(e);
                }
            }
        Assert.All(all, events => Assert.Equal(80, events.Count));
        var schedules = all.Select((events, i) => new ProCompetitionEvidenceService.Schedule($"T{i}", null, 2027, 2, 82, 0, 0, [], events.ToArray())).ToArray();
        var game = Game(); game.Id = "synthetic-pair-0"; game.HomeTeamAbbreviation = "T0"; game.AwayTeamAbbreviation = "T1";
        var context = ProHypeTests.Evidence(game);
        context.HomeProgress.SeedingUnresolved = context.AwayProgress.SeedingUnresolved = null;
        ProCompetitionEvidenceService.ApplySeedingCertificates(context, schedules);
        Assert.Null(context.HomeProgress.SeedingUnresolved); Assert.Null(context.AwayProgress.SeedingUnresolved);
    }
}
