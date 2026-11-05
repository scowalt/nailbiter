using backend.Models;
using backend.Scoring;
using backend.Services;
using Xunit;

namespace backend.tests;

public sealed class QualificationOnlyStakesTests
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

    public static IEnumerable<object?[]> SeedingCases()
    {
        foreach (var sport in new[] { "nfl", "nba", "nhl", "mlb" })
        foreach (var home in new bool?[] { null, false, true })
        foreach (var away in new bool?[] { null, false, true })
            yield return [sport, home, away];
    }

    [Theory]
    [MemberData(nameof(SeedingCases))]
    public void Seeding_metadata_never_changes_regular_season_stakes_or_supplies_a_bonus(string sport, bool? home, bool? away)
    {
        var game = sport == "nfl" ? NflPlayoffLeverageTests.Game() : ProHypeTests.Game(sport);
        var evidence = ProHypeTests.Evidence(game);
        evidence.HomeProgress.Completed = evidence.AwayProgress.Completed = evidence.HomeProgress.Scheduled - 1;
        evidence.HomeProgress.SeedingUnresolved = home;
        evidence.AwayProgress.SeedingUnresolved = away;
        if (sport == "nfl") evidence.NflPlayoffLeverage = NflPlayoffLeverageTests.SyntheticEvidence(game, Now, swing: .02);
        var result = Score(game, evidence, Q(.99), Q(.99));
        Assert.NotNull(result.Total);
        Assert.Equal(sport == "nfl" ? 1.6 : 3.04595910896665, result.Stakes.Value!.Value, 10);
        Assert.DoesNotContain("seeding", result.Stakes.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sport is "nba" or "mlb" ? 96.63793103448276 : 96.84979838709677, result.Quality.Value!.Value, 10);
        Assert.Equal(100, result.Closeness.Value);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(.01, 3.04595910896665)]
    [InlineData(.25, 36.317322592469374)]
    [InlineData(.365, 42.95732706774892)]
    [InlineData(.5, 45.59340347444945)]
    [InlineData(.75, 36.317322592469374)]
    [InlineData(.249999999999999, 36.317322592469296)]
    [InlineData(.750000000000001, 36.317322592469296)]
    [InlineData(.99, 3.04595910896665)]
    [InlineData(1, 0)]
    public void Valid_scalar_probabilities_keep_their_full_qualification_term_including_extremes(double probability, double expected)
    {
        var game = ProHypeTests.Game();
        var evidence = ProHypeTests.Evidence(game);
        evidence.HomeProgress.Completed = evidence.AwayProgress.Completed = 83;
        evidence.HomeProgress.SeedingUnresolved = evidence.AwayProgress.SeedingUnresolved = null;
        var result = Score(game, evidence, Q(probability), Q(probability));
        Assert.Equal(expected, result.Stakes.Value!.Value, 11);
        Assert.NotNull(result.Total);
        Assert.True(result.Quality.Value.HasValue && result.Closeness.Value.HasValue);
        Assert.Contains("qualification-only", result.Stakes.Explanation);
    }

    [Theory]
    [InlineData("nhl", 84)]
    [InlineData("nhl", 82)]
    [InlineData("nba", 82)]
    [InlineData("mlb", 162)]
    public void Verified_qualification_and_progress_preserve_each_supported_season_denominator(string sport, int length)
    {
        var game = ProHypeTests.Game(sport);
        if (sport == "nhl" && length == 82) game.SeasonYear = 2026;
        var evidence = ProHypeTests.Evidence(game);
        Assert.Equal(length, evidence.HomeProgress.Scheduled);
        evidence.HomeProgress.Completed = length - 1;
        Assert.Equal(45.59340347444945, Score(game, evidence, Q(.5), Q(.99)).Stakes.Value!.Value, 10);
    }

    [Theory]
    [InlineData("nfl", 0)]
    [InlineData("nfl", 1)]
    [InlineData("nba", 0)]
    [InlineData("nba", 1)]
    [InlineData("nhl", 0)]
    [InlineData("nhl", 1)]
    [InlineData("mlb", 0)]
    [InlineData("mlb", 1)]
    public void Trusted_qualification_finals_contribute_zero_without_seeding_or_unrelated_progress(string sport, double probability)
    {
        var game = ProHypeTests.Game(sport);
        var evidence = ProHypeTests.Evidence(game);
        evidence.HomeProgress = new(); evidence.AwayProgress = new();
        foreach (var seeding in new bool?[] { null, false, true })
        {
            evidence.HomeProgress.SeedingUnresolved = evidence.AwayProgress.SeedingUnresolved = seeding;
            var result = Score(game, evidence, Q(probability, true, Now.AddYears(-1)), Q(probability, true));
            Assert.Equal(0, result.Stakes.Value);
            Assert.NotNull(result.Total);
        }
    }

    [Theory]
    [InlineData("nba", 62, 12.778938307019486)]
    [InlineData("nhl", 64, 12.778938307019486)]
    [InlineData("mlb", 122, 9.104755493543080)]
    public void Long_season_stakes_follow_games_remaining_not_the_fraction_of_the_season_played(string sport, int completed, double expected)
    {
        var game = ProHypeTests.Game(sport);
        var evidence = ProHypeTests.Evidence(game);
        evidence.HomeProgress.Completed = evidence.AwayProgress.Completed = completed;
        var result = Score(game, evidence, Q(.5), Q(.5));
        Assert.Equal(expected, result.Stakes.Value!.Value, 10);
        Assert.Contains("estimated playoff swing", result.Stakes.Explanation);
    }

    [Fact]
    public void The_larger_qualification_contribution_uses_each_teams_own_progress_and_is_symmetric()
    {
        var game = ProHypeTests.Game();
        var evidence = ProHypeTests.Evidence(game);
        evidence.AwayProgress.Completed = 83;
        var result = Score(game, evidence);
        Assert.Equal(32.47149697414086, result.Stakes.Value!.Value, 10);
        (evidence.HomeProgress, evidence.AwayProgress) = (evidence.AwayProgress, evidence.HomeProgress);
        Assert.Equal(result.Stakes.Value, Score(game, evidence, Q(.795), Q(.365)).Stakes.Value);
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
    public void Removing_seeding_does_not_bypass_required_evidence_validation(string problem)
    {
        var game = ProHypeTests.Game();
        var evidence = ProHypeTests.Evidence(game);
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
        foreach (var seeding in new bool?[] { null, false, true })
        {
            evidence.HomeProgress.SeedingUnresolved = evidence.AwayProgress.SeedingUnresolved = seeding;
            var result = Score(game, evidence, Q(.5), away, completed);
            Assert.Null(result.Stakes.Value);
            Assert.Null(result.Total);
            Assert.NotNull(result.Quality.Value);
            Assert.NotNull(result.Closeness.Value);
        }
    }

    [Fact]
    public void Legacy_JSON_does_not_retroactively_gain_verified_progress_from_seeding_metadata()
    {
        var game = ProHypeTests.Game();
        var original = ProHypeTests.Evidence(game);
        var json = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(original))!;
        json["HomeProgress"]!.AsObject().Remove("ScheduleVerified");
        json["AwayProgress"]!.AsObject().Remove("ScheduleVerified");
        var legacy = ProGameEvidence.Parse(json.ToJsonString())!;
        Assert.False(legacy.HomeProgress.ScheduleVerified);
        Assert.Null(Score(game, legacy).Stakes.Value);
        Assert.Contains("awaiting competition refresh", Score(game, legacy).Stakes.Explanation);
        Assert.NotNull(Score(game, original).Stakes.Value);
    }
}
