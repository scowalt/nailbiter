using backend.Models;
using backend.Scoring;
using backend.Services;
using Xunit;

namespace backend.tests;

public sealed class ProStakesBoundsTests
{
    private static readonly DateTime Now = ProHypeTests.Now;
    private static ProQualification Q(double? probability = .365, bool final = false, DateTime? observed = null)
    {
        var at = observed ?? Now;
        return new(probability, final, at, final ? null : at.Add(PlayoffRatingPolicy.maxAge), "kalshi", ProQualificationEvidenceKind.Kalshi);
    }
    private static ProHypeResult Score(Game game, ProGameEvidence evidence, ProQualification? home = null, ProQualification? away = null,
        IEnumerable<string>? completed = null, DateTime? now = null) =>
        ProHypePolicy.Calculate(game, evidence, ProHypeTests.Ranks(game), home ?? Q(), away ?? Q(.795), ProHypeTests.Odds(game), completed ?? [], now ?? Now);
    private static ProGameEvidence Unknown(Game game)
    {
        var evidence = ProHypeTests.Evidence(game);
        evidence.HomeProgress.SeedingUnresolved = evidence.AwayProgress.SeedingUnresolved = null;
        return evidence;
    }

    [Theory]
    [InlineData(.25, true)]
    [InlineData(.365, true)]
    [InlineData(.5, true)]
    [InlineData(.75, true)]
    [InlineData(.249999999999999, false)]
    [InlineData(.750000000000001, false)]
    [InlineData(.99, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void Dominance_uses_exact_unrounded_bounds_not_price_endpoints_or_rounded_scores(double probability, bool exact)
    {
        var game = ProHypeTests.Game();
        var evidence = Unknown(game);
        var result = Score(game, evidence, Q(probability), Q(probability));
        Assert.Equal(exact, result.Stakes.Value.HasValue);
        Assert.Equal(exact, result.Total.HasValue);
        Assert.True(result.Quality.Value.HasValue);
        Assert.True(result.Closeness.Value.HasValue);
        Assert.Null(evidence.HomeProgress.SeedingUnresolved);
        Assert.Null(evidence.AwayProgress.SeedingUnresolved);
        if (exact) Assert.Contains("Exact Stakes: verified terms dominate", result.Stakes.Explanation);
        else Assert.Contains("seeding remains unknown", result.Stakes.Explanation);
    }

    [Theory]
    [InlineData("nhl", 84)]
    [InlineData("nhl", 82)]
    [InlineData("nba", 82)]
    [InlineData("nfl", 17)]
    [InlineData("mlb", 162)]
    public void Same_formula_proof_applies_to_each_audited_season_without_changing_complete_scores(string sport, int length)
    {
        var game = ProHypeTests.Game(sport);
        if (sport == "nhl" && length == 82) game.SeasonYear = 2026;
        var evidence = Unknown(game);
        Assert.Equal(length, evidence.HomeProgress.Scheduled);
        evidence.HomeProgress.Completed = length - 1;
        var result = Score(game, evidence, Q(.5), Q(.99));
        Assert.Equal(80, result.Stakes.Value);
        evidence.HomeProgress.SeedingUnresolved = evidence.AwayProgress.SeedingUnresolved = true;
        Assert.Equal(result.Stakes.Value, Score(game, evidence, Q(.5), Q(.99)).Stakes.Value);
    }

    [Fact]
    public void Bounds_match_enumeration_of_every_unknown_seeding_assignment_and_are_symmetric()
    {
        var game = ProHypeTests.Game();
        var evidence = Unknown(game);
        var probabilities = new[] { Q(0), Q(.01), Q(.25), Q(.365), Q(.5), Q(.75), Q(.795), Q(.99), Q(1), Q(0, true), Q(1, true) };
        foreach (var hg in new[] { 0, 1, 41, 83 })
        foreach (var ag in new[] { 0, 1, 41, 83 })
        foreach (var hq in probabilities)
        foreach (var aq in probabilities)
        foreach (var hs in new bool?[] { null, false, true })
        foreach (var @as in new bool?[] { null, false, true })
        {
            evidence.HomeProgress.Completed = hg;
            evidence.AwayProgress.Completed = ag;
            var values = new List<double>();
            foreach (var resolvedHome in hs.HasValue ? new[] { hs.Value } : new[] { false, true })
            foreach (var resolvedAway in @as.HasValue ? new[] { @as.Value } : new[] { false, true })
            {
                evidence.HomeProgress.SeedingUnresolved = resolvedHome;
                evidence.AwayProgress.SeedingUnresolved = resolvedAway;
                values.Add(Score(game, evidence, hq, aq).Stakes.Value!.Value);
            }
            evidence.HomeProgress.SeedingUnresolved = hs;
            evidence.AwayProgress.SeedingUnresolved = @as;
            var actual = Score(game, evidence, hq, aq).Stakes.Value;
            var expected = values.Min() == values.Max() ? values[0] : (double?)null;
            Assert.Equal(expected, actual);
            (evidence.HomeProgress, evidence.AwayProgress) = (evidence.AwayProgress, evidence.HomeProgress);
            Assert.Equal(actual, Score(game, evidence, aq, hq).Stakes.Value);
            (evidence.HomeProgress, evidence.AwayProgress) = (evidence.AwayProgress, evidence.HomeProgress);
        }
    }

    [Fact]
    public void Greater_away_urgency_can_prevent_dominance_even_with_home_probability_in_the_middle_band()
    {
        var game = ProHypeTests.Game();
        var evidence = Unknown(game);
        evidence.AwayProgress.Completed = 83;
        Assert.Null(Score(game, evidence).Stakes.Value);
        evidence.AwayProgress.SeedingUnresolved = false;
        Assert.NotNull(Score(game, evidence).Stakes.Value);
        evidence.AwayProgress.SeedingUnresolved = true;
        Assert.Equal(40, Score(game, evidence, Q(.99), Q(1, true)).Stakes.Value);
    }

    [Theory]
    [InlineData("missing-quote")]
    [InlineData("expired-quote")]
    [InlineData("future-quote")]
    [InlineData("invalid-quote")]
    [InlineData("invalid-final")]
    [InlineData("missing-progress")]
    [InlineData("unverified-schedule")]
    [InlineData("invalid-progress")]
    [InlineData("invalid-length")]
    [InlineData("expired-context")]
    [InlineData("future-context")]
    [InlineData("wrong-season")]
    [InlineData("wrong-team")]
    [InlineData("wrong-start")]
    [InlineData("wrong-game")]
    [InlineData("wrong-phase")]
    [InlineData("unverified-start")]
    [InlineData("new-result")]
    public void Dominance_does_not_bypass_required_evidence_validation(string problem)
    {
        var game = ProHypeTests.Game();
        var evidence = Unknown(game);
        var away = Q(.795);
        string[] completed = [];
        switch (problem)
        {
            case "missing-quote": away = Q(null); break;
            case "expired-quote": away = Q(.795, observed: Now.AddHours(-1)); break;
            case "future-quote": away = Q(.795, observed: Now.AddTicks(1)); break;
            case "invalid-quote": away = Q(double.NaN); break;
            case "invalid-final": away = Q(.795, true); break;
            case "missing-progress": evidence.AwayProgress.Completed = null; break;
            case "unverified-schedule": evidence.AwayProgress.ScheduleVerified = false; break;
            case "invalid-progress": evidence.AwayProgress.Completed = 84; break;
            case "invalid-length": evidence.AwayProgress.Scheduled = 0; break;
            case "expired-context": evidence.ObservedAt = Now.AddHours(-1); break;
            case "future-context": evidence.ObservedAt = Now.AddTicks(1); break;
            case "wrong-season": evidence.Season--; break;
            case "wrong-team": evidence.Away = "BOS"; break;
            case "wrong-start": evidence.ScheduledStart = game.ScheduledStart.AddDays(1); break;
            case "wrong-game": evidence.GameId = "different"; break;
            case "wrong-phase": evidence.Phase = 3; break;
            case "unverified-start": evidence.VerifiedUnstarted = false; break;
            case "new-result": completed = ["not-in-evidence"]; break;
        }
        var result = Score(game, evidence, Q(.5), away, completed);
        Assert.Null(result.Stakes.Value);
        Assert.Null(result.Total);
        Assert.NotNull(result.Quality.Value);
        Assert.NotNull(result.Closeness.Value);
    }

    [Fact]
    public void Legacy_JSON_does_not_retroactively_gain_the_schedule_consistency_guarantee()
    {
        var game = ProHypeTests.Game();
        var original = Unknown(game);
        var json = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(original))!;
        json["HomeProgress"]!.AsObject().Remove("ScheduleVerified");
        json["AwayProgress"]!.AsObject().Remove("ScheduleVerified");
        var legacy = ProGameEvidence.Parse(json.ToJsonString())!;
        Assert.False(legacy.HomeProgress.ScheduleVerified);
        var result = Score(game, legacy);
        Assert.Null(result.Stakes.Value);
        Assert.Contains("awaiting competition refresh", result.Stakes.Explanation);
        Assert.NotNull(Score(game, original).Stakes.Value);
        legacy.HomeProgress.SeedingUnresolved = legacy.AwayProgress.SeedingUnresolved = true;
        Assert.Equal(Score(game, original).Stakes.Value, Score(game, legacy).Stakes.Value);
    }

    [Fact]
    public void Preseason_postseason_and_started_games_do_not_enter_the_bounds_path()
    {
        var game = ProHypeTests.Game();
        var evidence = Unknown(game);
        game.SeasonType = evidence.Phase = 1;
        Assert.Equal(0, Score(game, evidence, Q(null), Q(null)).Stakes.Value);
        game.SeasonType = evidence.Phase = 3;
        Assert.Null(Score(game, evidence).Stakes.Value);
        evidence.Round = 4;
        evidence.HomeElimination = evidence.AwayElimination = true;
        Assert.Equal(100, Score(game, evidence, Q(null), Q(null)).Stakes.Value);
        game.SeasonType = evidence.Phase = 2;
        game.Status = "final";
        Assert.Null(Score(game, evidence).Stakes.Value);
        game.Status = "scheduled";
        game.ActualStart = Now;
        Assert.Null(Score(game, evidence).Stakes.Value);
        game.ActualStart = null;
        game.IsHistoricalImport = true;
        Assert.Null(Score(game, evidence).Stakes.Value);
    }
}
