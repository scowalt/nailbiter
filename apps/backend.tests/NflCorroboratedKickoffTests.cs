using AngleSharp.Html.Parser;
using System.Text.Json;
using System.Text.Json.Nodes;
using backend.Models;
using backend.Scoring;
using backend.Services;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace backend.tests;

public sealed class NflCorroboratedKickoffTests
{
    internal static Game Game(int minutes = 20)
    {
        var game = NflPlayoffLeverageTests.Game();
        game.Id = "control-7-DET-GB";
        game.ScheduledStart = game.ScheduledStart.AddMinutes(minutes);
        return game;
    }

    internal static ProCompetitionEvidenceService.Schedule[] Schedules(Game game) => NflPlayoffLeverageTests.Schedules().Select(s => s with
    {
        Events = s.Events.Select(e =>
        {
            if (e.GetProperty("id").GetString() != game.Id) return e;
            var copy = JsonNode.Parse(e.GetRawText())!;
            copy["date"] = game.ScheduledStart;
            copy["timeValid"] = true;
            return JsonSerializer.SerializeToElement(copy);
        }).ToArray()
    }).ToArray();

    internal static ProHypeResult Score(Game game, NflPlayoffLeverageEvidence evidence, DateTime? now = null)
    {
        var context = NflPlayoffLeverageTests.Context(game);
        context.NflPlayoffLeverage = evidence;
        var unavailable = new ProQualification(null, false, null, null, "unavailable", ProQualificationEvidenceKind.Unavailable);
        return ProHypePolicy.Calculate(game, context, NflPlayoffLeverageTests.Ranks(game), unavailable, unavailable,
            NflPlayoffLeverageTests.Odds(game), [], now ?? NflPlayoffLeverageTests.Now);
    }

    private static ProCompetitionEvidenceService.Schedule[] Change(ProCompetitionEvidenceService.Schedule[] schedules,
        string id, Action<JsonNode> change, bool firstOnly = false)
    {
        var changed = false;
        return schedules.Select(s => s with { Events = s.Events.Select(e =>
        {
            if (e.GetProperty("id").GetString() != id || firstOnly && changed) return e;
            changed = true;
            var copy = JsonNode.Parse(e.GetRawText())!;
            change(copy);
            return JsonSerializer.SerializeToElement(copy);
        }).ToArray() }).ToArray();
    }

    private static string SourceTime(string body, string text)
    {
        using var doc = new HtmlParser().ParseDocument(body);
        var row = doc.QuerySelectorAll("tr").Single(r => r.QuerySelector("td:nth-child(1) a")?.GetAttribute("href") == "#lions"
            && r.QuerySelector("td:nth-child(3) a")?.GetAttribute("href") == "#packers");
        row.Children[3].TextContent = text;
        return doc.DocumentElement.OuterHtml;
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(-20)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(30)]
    public async Task The_same_verified_next_contest_can_use_forecasts_with_a_corroborated_kickoff_difference(int minutes)
    {
        var game = Game(minutes);
        var evidence = (await NflPlayoffLeverageTests.Collector(new()).CollectAsync([game], Schedules(game), default))[game.Id]!;
        Assert.True(evidence.ScheduleVerified, evidence.Reason);
        Assert.Equal(game.ScheduledStart, evidence.ScheduledStart);
        Assert.Equal(2, evidence.Version);
        var match = Assert.IsType<NflCorroboratedKickoff>(evidence.CorroboratedKickoff);
        Assert.Equal(NflPlayoffLeverageTests.Game().ScheduledStart, match.SourceScheduledStart);
        Assert.Equal(game.ScheduledStart, match.EspnScheduledStart);
        Assert.Equal(7, match.Week);
        Assert.Equal(game.Id, match.HomeGameId);
        Assert.Equal(game.Id, match.AwayGameId);
        Assert.Equal(game.Id, match.HomeNextGameId);
        Assert.Equal(game.Id, match.AwayNextGameId);
        Assert.NotNull(evidence.HomeOdds);
        Assert.NotNull(evidence.AwayOdds);
        var scored = Score(game, evidence);
        Assert.Equal(20, scored.Stakes.Value!.Value, 8);
        Assert.Equal(58.3933971774, scored.Total!.Value, 10);
    }

    [Fact]
    public async Task Corroborated_kickoff_facts_are_bound_to_the_conditional_evidence_revision()
    {
        var game = Game();
        var evidence = (await NflPlayoffLeverageTests.Collector(new()).CollectAsync([game], Schedules(game), default))[game.Id]!;
        var before = ProEvidenceReference.Conditional(evidence, game.HomeTeamAbbreviation, NflPlayoffLeverageTests.Now, ProEvidenceState.Accepted);
        evidence.CorroboratedKickoff!.SourceScheduledStart = evidence.CorroboratedKickoff.SourceScheduledStart.AddMinutes(-1);
        Assert.NotNull(Score(game, evidence).Total);
        var after = ProEvidenceReference.Conditional(evidence, game.HomeTeamAbbreviation, NflPlayoffLeverageTests.Now, ProEvidenceState.Accepted);
        Assert.NotEqual(before.Revision, after.Revision);
    }

    [Theory]
    [InlineData(-60)]
    [InlineData(-31)]
    [InlineData(31)]
    [InlineData(60)]
    public async Task A_large_kickoff_change_cannot_borrow_next_game_forecasts(int minutes)
    {
        var game = Game(minutes);
        var handler = new NflPlayoffLeverageTests.Handler();
        var evidence = (await NflPlayoffLeverageTests.Collector(handler).CollectAsync([game], Schedules(game), default))[game.Id]!;
        Assert.False(evidence.ScheduleVerified);
        Assert.Null(evidence.HomeOdds);
        Assert.Null(evidence.AwayOdds);
        Assert.Null(Score(game, evidence).Total);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task The_thirty_minute_limit_is_inclusive_but_not_rounded(int direction)
    {
        var game = Game(30 * direction);
        game.ScheduledStart = game.ScheduledStart.AddTicks(direction);
        var evidence = (await NflPlayoffLeverageTests.Collector(new()).CollectAsync([game], Schedules(game), default))[game.Id]!;
        Assert.False(evidence.ScheduleVerified);
        Assert.Null(Score(game, evidence).Stakes.Value);
    }

    [Theory]
    [InlineData("missing-game-id")]
    [InlineData("missing-time-valid")]
    [InlineData("false-time-valid")]
    [InlineData("both-game-start-disagree")]
    [InlineData("inconsistent-copies")]
    [InlineData("wrong-season")]
    [InlineData("wrong-phase")]
    [InlineData("missing-week")]
    [InlineData("wrong-week")]
    [InlineData("started")]
    [InlineData("postponed")]
    [InlineData("score")]
    [InlineData("bad-score")]
    [InlineData("home-not-next")]
    [InlineData("away-not-next")]
    public async Task Corroboration_requires_the_actual_unstarted_target_in_both_ESPN_schedules(string problem)
    {
        var game = Game();
        var schedules = Schedules(game);
        if (problem == "missing-game-id") game.Id += "-other";
        else if (problem.EndsWith("-not-next", StringComparison.Ordinal))
        {
            var team = problem == "home-not-next" ? "DET" : "GB";
            var next = schedules.Single(s => s.Team == team).Events.Single(e => e.GetProperty("week").GetProperty("number").GetInt32() == 8);
            schedules = Change(schedules, next.GetProperty("id").GetString()!, e => e["date"] = NflPlayoffLeverageTests.Now.AddHours(1));
        }
        else schedules = Change(schedules, game.Id, e =>
        {
            switch (problem)
            {
                case "missing-time-valid": e.AsObject().Remove("timeValid"); break;
                case "false-time-valid": e["timeValid"] = false; break;
                case "both-game-start-disagree":
                case "inconsistent-copies": e["date"] = game.ScheduledStart.AddMinutes(1); break;
                case "wrong-season": e["season"]!["year"] = 2025; break;
                case "wrong-phase": e["seasonType"]!["type"] = 3; break;
                case "missing-week": e.AsObject().Remove("week"); break;
                case "wrong-week": e["week"]!["number"] = 8; break;
                case "started":
                    e["competitions"]![0]!["status"]!["type"]!["name"] = "STATUS_IN_PROGRESS";
                    e["competitions"]![0]!["status"]!["type"]!["state"] = "in";
                    e["competitions"]![0]!["status"]!["period"] = 1; break;
                case "postponed": e["competitions"]![0]!["status"]!["type"]!["name"] = "STATUS_POSTPONED"; break;
                case "score": e["competitions"]![0]!["competitors"]![0]!["score"]!["value"] = "1"; break;
                case "bad-score": e["competitions"]![0]!["competitors"]![0]!["score"]!["value"] = "unknown"; break;
            }
        }, problem == "inconsistent-copies");
        var handler = new NflPlayoffLeverageTests.Handler();
        var evidence = (await NflPlayoffLeverageTests.Collector(handler).CollectAsync([game], schedules, default))[game.Id]!;
        Assert.False(evidence.ScheduleVerified, evidence.Reason);
        Assert.Null(evidence.HomeOdds);
        Assert.Null(evidence.AwayOdds);
        Assert.Null(Score(game, evidence).Total);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_source_times_and_different_Eastern_dates_still_fail_closed(bool midnight)
    {
        var game = Game();
        if (midnight) game.ScheduledStart = new DateTime(2026, 10, 18, 4, 5, 0, DateTimeKind.Utc);
        var handler = new NflPlayoffLeverageTests.Handler { Transform = (path, body) => path.EndsWith("nflschedule.html")
            ? SourceTime(body, midnight ? "Sat Oct 17, 11:55 pm" : "Sun Oct 18, TBD") : body };
        var evidence = (await NflPlayoffLeverageTests.Collector(handler).CollectAsync([game], Schedules(game), default))[game.Id]!;
        Assert.False(evidence.ScheduleVerified, evidence.Reason);
        Assert.Null(Score(game, evidence).Total);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("legacy-version")]
    [InlineData("missing")]
    [InlineData("week")]
    [InlineData("source-utc")]
    [InlineData("espn-utc")]
    [InlineData("source-too-far")]
    [InlineData("same-start")]
    [InlineData("espn-start")]
    [InlineData("home-id")]
    [InlineData("away-id")]
    [InlineData("home-next")]
    [InlineData("away-next")]
    public async Task Every_read_requires_the_versioned_corroboration_not_just_a_verified_boolean(string problem)
    {
        var game = Game();
        var original = (await NflPlayoffLeverageTests.Collector(new()).CollectAsync([game], Schedules(game), default))[game.Id]!;
        var evidence = JsonSerializer.Deserialize<NflPlayoffLeverageEvidence>(JsonSerializer.Serialize(original))!;
        var match = evidence.CorroboratedKickoff!;
        switch (problem)
        {
            case "version": evidence.Version = 3; break;
            case "legacy-version": evidence.Version = 1; break;
            case "missing": evidence.CorroboratedKickoff = null; break;
            case "week": match.Week++; break;
            case "source-utc": match.SourceScheduledStart = DateTime.SpecifyKind(match.SourceScheduledStart, DateTimeKind.Unspecified); break;
            case "espn-utc": match.EspnScheduledStart = DateTime.SpecifyKind(match.EspnScheduledStart, DateTimeKind.Unspecified); break;
            case "source-too-far": match.SourceScheduledStart = game.ScheduledStart.AddMinutes(-31); break;
            case "same-start": match.SourceScheduledStart = game.ScheduledStart; break;
            case "espn-start": match.EspnScheduledStart = game.ScheduledStart.AddTicks(1); break;
            case "home-id": match.HomeGameId += "-other"; break;
            case "away-id": match.AwayGameId += "-other"; break;
            case "home-next": match.HomeNextGameId += "-other"; break;
            case "away-next": match.AwayNextGameId += "-other"; break;
        }
        Assert.True(evidence.ScheduleVerified);
        Assert.Null(Score(game, evidence).Stakes.Value);
        Assert.Null(Score(game, evidence).Total);
    }

    [Fact]
    public async Task An_early_source_time_does_not_advance_the_authoritative_ESPN_start_cutoff()
    {
        var game = Game();
        var now = game.ScheduledStart.AddMinutes(-10);
        var handler = new NflPlayoffLeverageTests.Handler { ServerDate = now };
        var evidence = (await NflPlayoffLeverageTests.Collector(handler, new FakeTimeProvider(now))
            .CollectAsync([game], Schedules(game), default))[game.Id]!;
        Assert.True(evidence.ScheduleVerified, evidence.Reason);
        var context = NflPlayoffLeverageTests.Context(game);
        context.NflPlayoffLeverage = evidence;
        Assert.True(evidence.CorroboratedKickoff!.SourceScheduledStart < now);
        Assert.NotNull(NflPlayoffLeveragePolicy.Select(game, context, now));
        Assert.NotNull(NflPlayoffLeveragePolicy.Select(game, context, game.ScheduledStart.AddTicks(-1)));
        Assert.Null(NflPlayoffLeveragePolicy.Select(game, context, game.ScheduledStart));
        game.ActualStart = now;
        Assert.Null(NflPlayoffLeveragePolicy.Select(game, context, now));
    }

    [Fact]
    public async Task Corroboration_preserves_observation_expiry_result_coverage_and_the_canonical_start_cutoff()
    {
        var game = Game();
        var evidence = (await NflPlayoffLeverageTests.Collector(new()).CollectAsync([game], Schedules(game), default))[game.Id]!;
        var context = NflPlayoffLeverageTests.Context(game);
        context.NflPlayoffLeverage = evidence;
        Assert.NotNull(NflPlayoffLeveragePolicy.Select(game, context, evidence.ObservedAt.AddHours(1).AddTicks(-1)));
        Assert.Null(NflPlayoffLeveragePolicy.Select(game, context, evidence.ObservedAt.AddHours(1)));
        Assert.Null(NflPlayoffLeveragePolicy.Select(game, context, evidence.ObservedAt, ["new-final"]));
        evidence.ObservedAt = game.ScheduledStart.AddMinutes(-25);
        Assert.NotNull(NflPlayoffLeveragePolicy.Select(game, context, game.ScheduledStart.AddTicks(-1)));
        Assert.Null(NflPlayoffLeveragePolicy.Select(game, context, game.ScheduledStart));
        evidence.ObservedAt = game.ScheduledStart;
        Assert.Null(NflPlayoffLeveragePolicy.Select(game, context, evidence.ObservedAt));
    }
}
