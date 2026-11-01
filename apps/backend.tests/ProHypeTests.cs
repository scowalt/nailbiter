using System.Text.Json;
using System.Text.Json.Nodes;
using backend.Models;
using backend.Scoring;
using backend.Services;
using Xunit;

namespace backend.tests;

public sealed class ProHypeTests
{
    internal static readonly DateTime Now = new(2026, 9, 30, 19, 0, 0, DateTimeKind.Utc);
    internal static Game Game(string sport = "nhl") => new()
    {
        Id = "401891821",
        Sport = sport,
        HomeTeam = "Philadelphia",
        AwayTeam = "Pittsburgh",
        HomeTeamAbbreviation = "PHI",
        AwayTeamAbbreviation = "PIT",
        SeasonYear = sport is "nhl" or "nba" ? 2027 : 2026,
        SeasonType = 2,
        ScheduledStart = Now.AddHours(4.5)
    };
    internal static ProGameEvidence Evidence(Game game) => new()
    {
        GameId = game.Id,
        Sport = game.Sport,
        Season = game.SeasonYear!.Value,
        Phase = game.SeasonType,
        Home = game.HomeTeamAbbreviation,
        Away = game.AwayTeamAbbreviation,
        ScheduledStart = game.ScheduledStart,
        ObservedAt = Now,
        Source = "fixture: verified schedule",
        VerifiedUnstarted = true,
        HomeProgress = new() { Completed = 0, Scheduled = ProHypePolicy.ScheduledLength(game.Sport, game.SeasonYear.Value), ScheduleVerified = true, SeedingUnresolved = true },
        AwayProgress = new() { Completed = 0, Scheduled = ProHypePolicy.ScheduledLength(game.Sport, game.SeasonYear.Value), ScheduleVerified = true, SeedingUnresolved = true }
    };
    internal static TeamRating[] Ranks(Game game, int h = 1, int a = 2, string? source = null) =>
        new[] { (game.HomeTeamAbbreviation, h), (game.AwayTeamAbbreviation, a) }.Select(x => new TeamRating
        {
            Sport = game.Sport,
            TeamAbbreviation = x.Item1,
            Source = source ?? (game.Sport == "nhl" ? MoneyPuckRankings.Source : "powerrankingsguru"),
            RankPublishedAt = game.Sport == "nhl" ? Now : null,
            SourceSeason = game.SeasonYear,
            Season = game.SeasonYear!.Value,
            PowerRank = x.Item2,
            FetchedAt = Now
        }).ToArray();
    internal static PregameOdds Odds(Game game, double p = .5) => new()
    {
        GameId = game.Id,
        Source = "espn",
        SourceSeason = game.SeasonYear,
        ApplicableStart = game.ScheduledStart,
        HomeTeamAbbreviation = game.HomeTeamAbbreviation,
        AwayTeamAbbreviation = game.AwayTeamAbbreviation,
        HomeWinProbability = p,
        AwayWinProbability = 1 - p,
        FetchedAt = Now
    };
    private static ProQualification Q(double? p = .5, bool final = false, DateTime? observed = null,
        ProQualificationEvidenceKind kind = ProQualificationEvidenceKind.Kalshi, DateTime? expires = null)
    {
        var at = observed ?? Now;
        return new(p, final, at,
            final ? null : expires ?? at.Add(kind == ProQualificationEvidenceKind.PlayoffStatusModel ? PlayoffRatingPolicy.modelMaxAge : PlayoffRatingPolicy.maxAge),
            kind == ProQualificationEvidenceKind.PlayoffStatusModel ? "playoffstatus" : "kalshi", kind);
    }
    private static ProHypeResult Score(Game g, ProGameEvidence? e, TeamRating[]? ranks = null, PregameOdds? odds = null,
        ProQualification? home = null, ProQualification? away = null, DateTime? now = null, string[]? completed = null) =>
        ProHypePolicy.Calculate(g, e, ranks ?? Ranks(g), home ?? Q(), away ?? Q(), odds ?? Odds(g), completed ?? [], now ?? Now);
    internal static JsonElement Fixture(string file) => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ProHype", file + ".json"))).RootElement;

    [Theory]
    [InlineData(0, 1, 2, .5, false, 64)]
    [InlineData(0, 14, 15, .55, false, 47)]
    [InlineData(83, 16, 17, .55, false, 72)]
    [InlineData(0, 1, 2, .5, true, 99)]
    public void Exact_approved_calibrations(int played, int homeRank, int awayRank, double probability, bool championship, int expected)
    {
        var g = Game(); var e = Evidence(g); e.HomeProgress.Completed = e.AwayProgress.Completed = played;
        if (championship) { g.SeasonType = e.Phase = 3; e.Round = 4; e.HomeElimination = e.AwayElimination = true; }
        var result = Score(g, e, Ranks(g, homeRank, awayRank), Odds(g, probability));
        Assert.Equal(expected, Math.Round(result.Total!.Value));
        Assert.Equal(championship ? 100 : played == 83 ? 80 : .1524, result.Stakes.Value!.Value, championship || played == 83 ? 8 : 3);
    }

    [Theory]
    [InlineData("nfl", 17)]
    [InlineData("nba", 82)]
    [InlineData("nhl", 84)]
    [InlineData("mlb", 162)]
    public void Progress_denominator_and_zero_are_verified_not_calendar_guesses(string sport, int length)
    {
        var g = Game(sport); var e = Evidence(g);
        Assert.Equal(length, e.HomeProgress.Scheduled);
        var start = Score(g, e).Stakes.Value;
        Assert.InRange(start!.Value, 0.01, 1);
        e.HomeProgress.Completed = e.AwayProgress.Completed = length - 1;
        Assert.Equal(80, Score(g, e).Stakes.Value);
        foreach (var invalid in new int?[] { null, -1, length, length + 1 })
        {
            e.HomeProgress.Completed = invalid;
            var result = Score(g, e); Assert.Null(result.Stakes.Value); Assert.Null(result.Total);
            Assert.NotNull(result.Quality.Value); Assert.NotNull(result.Closeness.Value);
        }
    }

    [Theory]
    [InlineData(9, 30)]
    [InlineData(12, 31)]
    public void Calendar_boundary_does_not_change_stakes(int month, int day)
    {
        var g = Game(); g.ScheduledStart = new DateTime(2026, month, day, 23, 50, 0, DateTimeKind.Utc).AddDays(2);
        var e = Evidence(g); var t = g.ScheduledStart.AddDays(-2); e.ObservedAt = t;
        var before = Score(g, e, home: Q(observed: t), away: Q(observed: t), now: t).Stakes;
        var after = Score(g, e, home: Q(observed: t), away: Q(observed: t), now: t.AddMinutes(20)).Stakes;
        Assert.NotNull(before.Value); Assert.Equal(before.Value, after.Value);
    }

    [Fact]
    public void Seeding_applies_on_both_sides_of_clinching_and_unknown_is_not_zero()
    {
        var g = Game(); var e = Evidence(g); e.HomeProgress.Completed = 83; e.AwayProgress.Completed = 83;
        Assert.Equal(40, Score(g, e, home: Q(.99), away: Q(.99)).Stakes.Value);
        Assert.Equal(40, Score(g, e, home: Q(1, true), away: Q(1, true)).Stakes.Value);
        foreach (var q in new[] { Q(.99), Q(1, true) })
        {
            e.HomeProgress.SeedingUnresolved = null;
            e.AwayProgress.SeedingUnresolved = true;
            Assert.Equal(40, Score(g, e, home: q, away: q).Stakes.Value);  
            e.AwayProgress.SeedingUnresolved = null;
            Assert.Null(Score(g, e, home: q, away: q).Stakes.Value);
        }
        e.HomeProgress.SeedingUnresolved = e.AwayProgress.SeedingUnresolved = false;
        e.HomeProgress.Completed = e.AwayProgress.Completed = null;
        Assert.Equal(0, Score(g, e, home: Q(1, true), away: Q(1, true)).Stakes.Value);
        e.HomeProgress.SeedingUnresolved = null;
        Assert.Equal(0, Score(g, e, home: Q(0, true, Now.AddYears(-1)), away: Q(0, true)).Stakes.Value);
        Assert.Null(Score(g, e, home: Q(0), away: Q(0)).Stakes.Value);  
        e.AwayProgress = new() { Scheduled = 84, Completed = 83, SeedingUnresolved = true };
        Assert.Equal(80, Score(g, e, home: Q(0, true)).Stakes.Value);
    }

    [Theory]
    [InlineData(1, 2, 96.849798)]
    [InlineData(14, 15, 56.206597)]
    [InlineData(28, 19, 21.09375)]
    public void Midpoint_ranks_drive_quality_not_numerical_ratings(int h, int a, double expected)
    {
        var g = Game(); var ranks = Ranks(g, h, a);
        var quality = Score(g, Evidence(g), ranks).Quality.Value!.Value;
        Assert.Equal(expected, quality, 5);
        ranks[0].EloRating = 99999; ranks[1].BpiRating = -99999; ranks[0].ChampionshipProbability = 1;
        Assert.Equal(quality, Score(g, Evidence(g), ranks).Quality.Value);
    }

    [Fact]
    public void Ranking_pair_selection_never_mixes_sources_or_resurrects_superseded_rows()
    {
        var g = Game("nfl"); var fallback = Ranks(g, 14, 15); var preferred = Ranks(g, 1, 2, "espn_fpi");
        var e = Evidence(g);
        var expected = Score(g, e, preferred).Quality.Value;
        Assert.Equal(expected, Score(g, e, [.. fallback, .. preferred]).Quality.Value);
        preferred[1].SourceSeason = 2025;
        Assert.Equal(Score(g, e, fallback).Quality.Value, Score(g, e, [.. fallback, .. preferred]).Quality.Value);
        Assert.Null(Score(g, e, [fallback[1], preferred[0]]).Quality.Value);
        var newer = Ranks(g, 0, 2)[0]; newer.FetchedAt = Now.AddMinutes(1);
        Assert.Null(Score(g, e, [.. fallback, newer], now: Now.AddMinutes(1)).Quality.Value);
        foreach (var invalid in new int?[] { 0, 33, null })
        { fallback[0].PowerRank = invalid; Assert.Null(Score(g, e, fallback).Quality.Value); }
    }

    [Theory]
    [InlineData(1, false, false, 70)]
    [InlineData(2, true, false, 85)]
    [InlineData(3, true, true, 95)]
    [InlineData(1, true, true, 85)]
    [InlineData(4, true, false, 95)]
    [InlineData(4, true, true, 100)]
    public void Postseason_bands_require_actual_elimination_context(int round, bool h, bool a, double expected)
    {
        var g = Game(); g.SeasonType = 3; var e = Evidence(g); e.Round = round; e.HomeElimination = h; e.AwayElimination = a;
        Assert.Equal(expected, Score(g, e, home: Q(null), away: Q(null)).Stakes.Value);
        e.AwayElimination = null;
        Assert.Null(Score(g, e).Stakes.Value);
        g.SeasonType = e.Phase = 1;
        Assert.Equal(0, Score(g, e, home: Q(null), away: Q(null)).Stakes.Value);
    }

    [Theory]
    [InlineData("second_chance", 70)]
    [InlineData("knockout", 75)]
    [InlineData("berth_decider", 80)]
    public void Playin_values_are_not_generic_postseason(string kind, double expected)
    {
        var g = Game("nba"); g.SeasonType = 5; var e = Evidence(g); e.PlayIn = kind;
        Assert.Equal(expected, Score(g, e, home: Q(null), away: Q(null)).Stakes.Value);
        e.PlayIn = null; Assert.Null(Score(g, e).Stakes.Value);
    }

    [Fact]
    public void Common_time_expiry_new_results_and_partial_metrics()
    {
        var g = Game(); g.ScheduledStart = Now.AddDays(5); var e = Evidence(g); var odds = Odds(g);
        var result = Score(g, e, odds: odds, home: Q(null));
        Assert.Null(result.Total); Assert.Null(result.Stakes.Value); Assert.NotNull(result.Quality.Value); Assert.Equal(100, result.Closeness.Value);
        result = ProHypePolicy.Calculate(g, e, Ranks(g), Q(), Q(), null, [], Now);
        Assert.NotNull(result.Stakes.Value); Assert.NotNull(result.Quality.Value); Assert.Null(result.Closeness.Value); Assert.Null(result.Total);
        result = Score(g, e, now: Now.AddHours(1));
        Assert.Null(result.Stakes.Value); Assert.Null(result.Closeness.Value); Assert.NotNull(result.Quality.Value);
        Assert.Null(Score(g, e, now: Now.AddHours(48)).Quality.Value);
        Assert.Null(Score(g, e, now: Now.AddSeconds(-1)).Quality.Value);
        Assert.Null(Score(g, e, completed: ["new-result"]).Stakes.Value);
        e.CompletedGameIds = ["new-result"];
        Assert.NotNull(Score(g, e, completed: ["new-result"]).Stakes.Value);
        e.Season--; Assert.Null(Score(g, e).Stakes.Value);
        odds.ApplicableStart = odds.ApplicableStart!.Value.AddDays(-1); Assert.Null(Score(g, null, odds: odds).Closeness.Value);
        odds = Odds(g); odds.AwayWinProbability = .7; Assert.Null(Score(g, null, odds: odds).Closeness.Value);
    }

    [Fact]
    public void Qualification_estimates_use_selected_expiry_and_finals_remain_kalshi_only()
    {
        var g = Game(); var e = Evidence(g);
        var selectedDailyModel = new ProQualification(.5, false, Now.AddHours(-24), Now.AddHours(12), "playoffstatus", ProQualificationEvidenceKind.PlayoffStatusModel);
        var oldButAuthorizedModel = new ProQualification(.5, false, Now.AddHours(-37), Now.AddHours(1), "playoffstatus", ProQualificationEvidenceKind.PlayoffStatusModel);
        var shortExpiredModel = new ProQualification(.5, false, Now.AddMinutes(-10), Now.AddTicks(-1), "playoffstatus", ProQualificationEvidenceKind.PlayoffStatusModel);
        var missingExpiryModel = new ProQualification(.5, false, Now.AddMinutes(-10), null, "playoffstatus", ProQualificationEvidenceKind.PlayoffStatusModel);
        var shortExpiredKalshi = new ProQualification(.5, false, Now, Now.AddTicks(-1), "kalshi", ProQualificationEvidenceKind.Kalshi);
        var futureObservation = new ProQualification(.5, false, Now.AddTicks(1), Now.AddHours(1), "kalshi", ProQualificationEvidenceKind.Kalshi);
        var forgedOldFinal = new ProQualification(0, true, Now.AddYears(-1), null, "kalshi");

        Assert.NotNull(Score(g, e, home: selectedDailyModel, away: Q()).Stakes.Value);
        Assert.Null(Score(g, e, home: shortExpiredModel, away: Q()).Stakes.Value);
        Assert.Null(Score(g, e, home: missingExpiryModel, away: Q()).Stakes.Value);
        Assert.NotNull(Score(g, e, home: oldButAuthorizedModel, away: Q()).Stakes.Value);
        Assert.Null(Score(g, e, home: shortExpiredKalshi, away: Q()).Stakes.Value);
        Assert.Null(Score(g, e, home: futureObservation, away: Q()).Stakes.Value);
        Assert.Null(Score(g, e, home: forgedOldFinal, away: forgedOldFinal).Stakes.Value);
    }

    [Fact]
    public void Closeness_and_composite_are_independent_of_qualification_and_symmetric()
    {
        var g = Game(); var e = Evidence(g); e.Phase = g.SeasonType = 1;
        var before = Score(g, e, odds: Odds(g, .55), home: Q(.01), away: Q(.99));
        var after = Score(g, e, odds: Odds(g, .55), home: Q(null), away: Q(null));
        Assert.Equal(before.Total, after.Total); Assert.Equal(90, after.Closeness.Value!.Value, 8);
        Assert.Equal(after, Score(g, e, odds: Odds(g, .55), home: Q(null), away: Q(null)));
        Assert.Equal(Score(g, e, Ranks(g, 1, 2), Odds(g, .55)).Total!.Value, Score(g, e, Ranks(g, 2, 1), Odds(g, .45)).Total!.Value, 10);
        g.ScheduledStart = Now; Assert.Null(Score(g, e).Total);
        g.ScheduledStart = Now.AddDays(1); g.ActualStart = Now; Assert.Null(Score(g, e).Quality.Value);
        g.ActualStart = null; g.IsHistoricalImport = true; Assert.Null(Score(g, e).Total);
    }

    [Fact]
    public void Synthetic_season_schedules_validate_real_counts_and_zero_progress()
    {
        var nhl = ProCompetitionEvidenceService.ParseSchedule(Fixture("nhl-schedule"), "nhl", 2027, 2, "PHI", Now);
        Assert.NotNull(nhl); Assert.Equal(84, nhl.Length); Assert.Equal(0, nhl.Completed);
        var nfl = ProCompetitionEvidenceService.ParseSchedule(Fixture("espnschedule"), "nfl", 2026, 2, "BUF", Now);
        Assert.NotNull(nfl); Assert.Equal(17, nfl.Length); Assert.Equal(3, nfl.Completed);
        Assert.Null(ProCompetitionEvidenceService.ParseSchedule(Fixture("nhl-schedule"), "nhl", 2026, 2, "PHI", Now));
        var board = Fixture("nhl-upcoming-board").GetProperty("events").EnumerateArray().Single(e => e.GetProperty("id").GetString() == Game().Id);
        var context = ProCompetitionEvidenceService.ParseEvent(Game(), board, [nhl], Now, "ESPN captured");
        Assert.True(context.VerifiedUnstarted); Assert.Equal(84, context.HomeProgress.Scheduled); Assert.Equal(0, context.HomeProgress.Completed);
        Assert.Null(context.HomeProgress.SeedingUnresolved); Assert.Null(Score(Game(), context).Stakes.Value);
    }

    [Fact]
    public void Overtime_makeup_suspended_canceled_and_missing_schedule_fields_are_not_invented_results()
    {
        var root = JsonNode.Parse(Fixture("nhl-schedule").GetRawText())!;
        var events = root["events"]!.AsArray();
        var e = events[0]!; e["date"] = Now.AddDays(-1).ToString("O");
        var c = e["competitions"]![0]!; c["status"]!["type"]!["name"] = "STATUS_FINAL"; c["status"]!["type"]!["state"] = "post";
        c["status"]!["type"]!["completed"] = true; c["status"]!["period"] = 4;
        c["competitors"]![0]!["score"] = new JsonObject { ["value"] = 2 }; c["competitors"]![1]!["score"] = new JsonObject { ["value"] = 3 };
        ProCompetitionEvidenceService.Schedule? Parse() => ProCompetitionEvidenceService.ParseSchedule(JsonSerializer.SerializeToElement(root), "nhl", 2027, 2, "PHI", Now);
        Assert.Equal(1, Parse()!.Completed); Assert.Equal(1, Parse()!.Points);  
        c["status"]!["type"]!["name"] = "STATUS_SUSPENDED"; c["status"]!["type"]!["completed"] = false;
        Assert.Equal(0, Parse()!.Completed);
        c["status"]!["type"]!["name"] = "STATUS_POSTPONED"; Assert.Equal(84, Parse()!.Length);
        e["date"] = Now.AddDays(2).ToString("O"); c["status"]!["type"]!["name"] = "STATUS_SCHEDULED";
        Assert.Equal(84, Parse()!.Length); Assert.Equal(0, Parse()!.Completed);
        c["status"]!["type"]!["name"] = "STATUS_CANCELED"; Assert.Equal(83, Parse()!.Length);
        events.Add(e.DeepClone()); Assert.Null(Parse()); events.RemoveAt(events.Count - 1);
        events.RemoveAt(1); Assert.Null(Parse());  
    }

    [Theory]
    [InlineData("nba-board", 0, "knockout")]
    [InlineData("nba-board", 1, "second_chance")]
    [InlineData("nba-decider-board", 0, "berth_decider")]
    public void Synthetic_playin_contract_labels_are_interpreted_not_generic_flags(string file, int index, string expected)
    {
        var e = Fixture(file).GetProperty("events")[index];
        var g = GameFromEvent(e, "nba");
        var result = ProCompetitionEvidenceService.ParseEvent(g, e, [], Now, "synthetic ESPN-format");
        Assert.Equal(expected, result.PlayIn); Assert.False(result.VerifiedUnstarted);  
    }

    [Fact]
    public void Complete_schedule_bounds_prove_real_seeding_consequences_without_rank_or_clinch_flags()
    {
        var all = Enumerable.Range(0, 32).Select(_ => new List<JsonElement>()).ToArray();
        var id = 0;
        for (var h = 0; h < 32; h++)
            for (var a = h + 1; a < 32; a++)
            {
                var distance = Math.Min(a - h, 32 - (a - h));
                for (var meeting = 0; meeting < (distance <= 11 ? 3 : 2); meeting++)
                {
                    var e = JsonSerializer.SerializeToElement(new
                    {
                        id = $"s-{id++}",
                        date = Now.AddDays(1),
                        competitions = new[] { new {
                    status = new { period = 0, type = new { name = "STATUS_SCHEDULED", state = "pre", completed = false } },
                    competitors = new[] { new { team = new { abbreviation = $"T{h}" } }, new { team = new { abbreviation = $"T{a}" } } }
                } }
                    });
                    all[h].Add(e); all[a].Add(e);
                }
            }
        Assert.All(all, events => Assert.Equal(84, events.Count));
        var schedules = all.Select((events, i) => new ProCompetitionEvidenceService.Schedule($"T{i}", "division", 2027, 2, 84, 0, 0, [], events.ToArray())).ToArray();
        var g = Game(); g.Id = "s-0"; g.HomeTeamAbbreviation = "T0"; g.AwayTeamAbbreviation = "T1";
        var evi = Evidence(g); evi.HomeProgress.SeedingUnresolved = evi.AwayProgress.SeedingUnresolved = null;
        ProCompetitionEvidenceService.ApplySeedingCertificates(evi, schedules);
        Assert.True(evi.HomeProgress.SeedingUnresolved); Assert.True(evi.AwayProgress.SeedingUnresolved);
        evi.HomeProgress.SeedingUnresolved = evi.AwayProgress.SeedingUnresolved = null;
        ProCompetitionEvidenceService.ApplySeedingCertificates(evi, schedules.Skip(1).ToArray());
        Assert.Null(evi.HomeProgress.SeedingUnresolved);  
    }

    [Fact]
    public void Synthetic_nfl_round_and_nhl_series_are_not_automatic_maximums()
    {
        var nfl = Fixture("nfl-board").GetProperty("events")[0];
        var g = GameFromEvent(nfl, "nfl");
        var context = ProCompetitionEvidenceService.ParseEvent(g, nfl, [], Now, "synthetic ESPN-format");
        Assert.Equal(2, context.Round); Assert.True(context.HomeElimination); Assert.True(context.AwayElimination);
        Assert.False(context.VerifiedUnstarted);

        var node = JsonNode.Parse(Fixture("nhl-board").GetProperty("events")[0].GetRawText())!;
        node["date"] = Now.AddHours(2).ToString("O"); node["season"]!["year"] = 2027;
        var c = node["competitions"]![0]!;
        c["status"] = new JsonObject { ["period"] = 0, ["type"] = new JsonObject { ["name"] = "STATUS_SCHEDULED", ["state"] = "pre", ["completed"] = false } };
        foreach (var team in c["competitors"]!.AsArray()) team!["score"] = "0";
        c["series"]!["completed"] = false; c["series"]!["competitors"]![0]!["wins"] = 3; c["series"]!["competitors"]![1]!["wins"] = 2;
        var ev = JsonSerializer.SerializeToElement(node); g = GameFromEvent(ev, "nhl");
        var prior = Enumerable.Range(0, 5).Select(i => JsonSerializer.SerializeToElement(new
        {
            id = $"prior-{i}",
            date = Now.AddDays(-1 - i),
            competitions = new[] { new {
            status = new { type = new { name = "STATUS_FINAL", state = "post", completed = true } }, competitors = new[] {
                new { team = new { abbreviation = g.HomeTeamAbbreviation }, winner = i < 3 }, new { team = new { abbreviation = g.AwayTeamAbbreviation }, winner = i >= 3 }
            } } }
        })).ToArray();
        var schedule = new ProCompetitionEvidenceService.Schedule(g.HomeTeamAbbreviation, null, 2027, 3, 7, 5, 0, prior.Select(x => x.GetProperty("id").GetString()!).ToArray(), [.. prior, ev]);
        context = ProCompetitionEvidenceService.ParseEvent(g, ev, [schedule], Now, "synthetic contract variant");
        Assert.True(context.VerifiedUnstarted); Assert.Equal(4, context.Round);
        Assert.False(context.HomeElimination); Assert.True(context.AwayElimination);
        Assert.Equal(95, Score(g, context).Stakes.Value);
        schedule = schedule with { Events = [.. prior.Skip(1), ev] };
        context = ProCompetitionEvidenceService.ParseEvent(g, ev, [schedule], Now, "incomplete history");
        Assert.Null(context.HomeElimination); Assert.Null(Score(g, context).Stakes.Value);
    }

    [Fact]
    public void Utah_provider_aliases_match_and_prior_NHL_season_keeps_82_games()
    {
        var g = Game(); g.HomeTeamAbbreviation = "UTA"; var rows = Ranks(g); rows[0].TeamAbbreviation = "UTAH";
        Assert.NotNull(Score(g, Evidence(g), rows).Quality.Value);
        var root = JsonNode.Parse(Fixture("nhl-schedule").GetRawText())!;
        root["team"]!["abbreviation"] = "UTAH";
        foreach (var e in root["events"]!.AsArray())
            foreach (var team in e!["competitions"]![0]!["competitors"]!.AsArray())
            {
                var abbreviation = team!["team"]!["abbreviation"]!.GetValue<string>();
                if (abbreviation == "PHI") team["team"]!["abbreviation"] = "UTA";
                else if (abbreviation is "UTA" or "UTAH") team["team"]!["abbreviation"] = "PHI";
            }
        var parsed = ProCompetitionEvidenceService.ParseSchedule(JsonSerializer.SerializeToElement(root), "nhl", 2027, 2, "UTAH", Now);
        Assert.NotNull(parsed); Assert.Equal(84, parsed.Length);
        g = Game(); g.SeasonYear = 2026; var evidence = Evidence(g);
        Assert.Equal(82, evidence.HomeProgress.Scheduled);
        evidence.HomeProgress.Completed = evidence.AwayProgress.Completed = 81;
        Assert.Equal(80, Score(g, evidence).Stakes.Value);
    }

    [Fact]
    public void Synthetic_round_labels_cover_all_four_series_rounds()
    {
        foreach (var capture in Fixture("postseason-rounds").EnumerateArray())
        {
            var e = capture.GetProperty("event"); var g = GameFromEvent(e, capture.GetProperty("sport").GetString()!);
            var context = ProCompetitionEvidenceService.ParseEvent(g, e, [], Now, capture.GetProperty("url").GetString()!);
            Assert.Equal(capture.GetProperty("round").GetInt32(), context.Round);
            Assert.False(context.VerifiedUnstarted);
        }
    }

    internal static Game GameFromEvent(JsonElement e, string sport)
    {
        var c = e.GetProperty("competitions")[0]; var teams = c.GetProperty("competitors").EnumerateArray().ToArray();
        var g = Game(sport); g.Id = e.GetProperty("id").GetString()!; g.ScheduledStart = DateTimeOffset.Parse(e.GetProperty("date").GetString()!).UtcDateTime;
        g.SeasonYear = e.GetProperty("season").GetProperty("year").GetInt32(); g.SeasonType = e.GetProperty("season").GetProperty("type").GetInt32();
        g.HomeTeamAbbreviation = teams.Single(t => t.GetProperty("homeAway").GetString() == "home").GetProperty("team").GetProperty("abbreviation").GetString()!;
        g.AwayTeamAbbreviation = teams.Single(t => t.GetProperty("homeAway").GetString() == "away").GetProperty("team").GetProperty("abbreviation").GetString()!;
        return g;
    }
}
