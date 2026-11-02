using backend.Models;
using backend.Services;

namespace backend.Scoring;

public sealed record ProMetric(double? Value, string Explanation);
public enum ProQualificationEvidenceKind { Scalar, Kalshi, PlayoffStatusModel, MoneyPuckModel, Unavailable }
internal sealed record ProQualification(double? Probability, bool IsFinal, DateTime? ObservedAt, DateTime? ExpiresAt = null,
    string Source = "qualification evidence", ProQualificationEvidenceKind EvidenceKind = ProQualificationEvidenceKind.Scalar);
internal sealed record ProHypeResult(ProMetric Stakes, ProMetric Quality, ProMetric Closeness, double? Total,
    NflPlayoffLeverageEvidence? NflPlayoffLeverage = null);

public static class ProHypePolicy
{
    private sealed record StakesBounds(double? Lower, double? Upper, string Explanation, bool UnknownSeeding = false);

    public static bool Supports(string sport) => sport is "nfl" or "nba" or "nhl" or "mlb";
    public static string TeamKey(string sport, string abbreviation) => sport == "nhl" && abbreviation == "UTA" ? "UTAH" : abbreviation;
    public static bool Fresh(DateTime observed, DateTime now, int hours) => observed <= now && now - observed < TimeSpan.FromHours(hours);
    public static bool Probability(double p) => double.IsFinite(p) && p >= 0 && p <= 1;

    public static int? TeamCount(string sport, int season) => (sport, season) switch
    {
        ("nfl", >= 2025 and <= 2026) or ("nhl", >= 2026 and <= 2027) => 32,
        ("nba", >= 2026 and <= 2027) or ("mlb", 2026) => 30,
        _ => null
    };
    public static int? ScheduledLength(string sport, int season) => (sport, season) switch
    {
        ("nfl", >= 2025 and <= 2026) => 17,
        ("nba", >= 2026 and <= 2027) or ("nhl", 2026) => 82,
        ("nhl", 2027) => 84,
        ("mlb", 2026) => 162,
        _ => null
    };

    public static bool EligibleRank(TeamRating r, string sport, int season, DateTime now) =>
        r.Sport == sport && r.Season == season && r.SourceSeason == season &&
        Fresh(r.FetchedAt, now, 48) && TeamCount(sport, season) is int n && r.PowerRank is int rank && rank >= 1 && rank <= n &&
        (r.Source != MoneyPuckRankings.Source || (sport == "nhl" && r.RankPublishedAt is DateTime published && published <= r.FetchedAt &&
            MoneyPuckRankings.EligiblePublication(published, now, season)));

    public static ProMetric Quality(Game game, IEnumerable<TeamRating> ratings, DateTime now)
    {
        if (game.SeasonYear is not int season || TeamCount(game.Sport, season) is not int n)
            return new(null, "Verified season and league team count unavailable");
        var rows = ratings.ToArray();
        var sources = game.Sport switch
        {
            "nfl" => new[] { "espn_fpi", "powerrankingsguru" },
            "nba" => new[] { "espn_bpi", "powerrankingsguru" },
            "nhl" => new[] { MoneyPuckRankings.Source },
            _ => new[] { "powerrankingsguru" }
        };
        string Publication(TeamRating row) => row.Source == MoneyPuckRankings.Source
            ? $"; rolling ranking published {row.RankPublishedAt?.ToString("O") ?? "unknown"} (seven-day limit; season from Eastern publication date)" : "";
        var observations = new List<string>();
        foreach (var source in sources)
        {
            TeamRating? Latest(string team) => rows.Where(r => r.Sport == game.Sport && r.Season == season && TeamKey(game.Sport, r.TeamAbbreviation) == TeamKey(game.Sport, team) && r.Source == source)
                .OrderByDescending(r => r.FetchedAt).ThenByDescending(r => r.Id).FirstOrDefault();
            var home = Latest(game.HomeTeamAbbreviation);
            var away = Latest(game.AwayTeamAbbreviation);
            foreach (var row in new[] { home, away }.OfType<TeamRating>())
                observations.Add($"{source} {row.TeamAbbreviation}, source season {row.SourceSeason?.ToString() ?? "unverified"}, observed {row.FetchedAt:O}{Publication(row)}");
            if (home is null || away is null || !EligibleRank(home, game.Sport, season, now) || !EligibleRank(away, game.Sport, season, now)) continue;
            double Strength(int rank) => 100.0 * (n - rank + 0.5) / n;
            var h = Strength(home.PowerRank!.Value);
            var a = Strength(away.PowerRank!.Value);
            return new(2 * h * a / (h + a), $"{source}: #{home.PowerRank} / #{away.PowerRank}; season {season}, {n} teams; observed {home.FetchedAt:O} / {away.FetchedAt:O}{Publication(home)}{(home.RankPublishedAt != away.RankPublishedAt ? Publication(away) : "")}");
        }
        return new(null, "No complete current-season same-source ranking pair fetched within 48 hours. " +
            (game.Sport == "nhl" ? "MoneyPuck requires a valid publication date less than seven days old. " : "") + string.Join("; ", observations));
    }

    public static bool EligibleOdds(Game game, PregameOdds? odds, DateTime now) => odds is not null &&
        odds.GameId == game.Id && odds.Source is "espn" or "kalshi" && odds.ApplicableStart == game.ScheduledStart &&
        odds.HomeTeamAbbreviation is not null && odds.AwayTeamAbbreviation is not null &&
        TeamKey(game.Sport, odds.HomeTeamAbbreviation) == TeamKey(game.Sport, game.HomeTeamAbbreviation) &&
        TeamKey(game.Sport, odds.AwayTeamAbbreviation) == TeamKey(game.Sport, game.AwayTeamAbbreviation) &&
        odds.SourceSeason == game.SeasonYear && odds.SourceSeason.HasValue &&
        Fresh(odds.FetchedAt, now, 1) && !odds.DrawWinProbability.HasValue &&
        Probability(odds.HomeWinProbability) && Probability(odds.AwayWinProbability) &&
        Math.Abs(odds.HomeWinProbability + odds.AwayWinProbability - 1) < 1e-8;

    public static double? Composite(string sport, double? stakes, double? quality, double? closeness)
    {
        if (!stakes.HasValue || !quality.HasValue || !closeness.HasValue) return null;
        var (s, q, c) = sport switch
        {
            "nhl" => (.35, .35, .30),
            "nba" => (.30, .40, .30),
            "nfl" => (.45, .35, .20),
            "mlb" => (.35, .30, .35),
            _ => throw new ArgumentException("Unsupported pro league", nameof(sport))
        };
        return s * stakes + q * quality + c * closeness;
    }

    internal static ProHypeResult Calculate(Game game, ProGameEvidence? evidence, IEnumerable<TeamRating> rankings,
        ProQualification home, ProQualification away, PregameOdds? odds, IEnumerable<string> knownCompletedIds, DateTime now)
    {
        ProMetric Missing(string why) => new(null, why);
        if (!Supports(game.Sport) || game.IsHistoricalImport || game.Status != "scheduled" || game.ActualStart.HasValue || game.ScheduledStart <= now || !game.SeasonYear.HasValue)
        {
            var missing = Missing("Hype requires a verified upcoming, unstarted game and season; current evidence cannot reconstruct historical hype");
            return new(missing, missing, missing, null);
        }
        var quality = Quality(game, rankings, now);
        var oddsNote = odds is null ? "No game-winner observation" : $"{odds.Source}, source season {odds.SourceSeason?.ToString() ?? "unverified"}, observed {odds.FetchedAt:O}";
        var closeness = EligibleOdds(game, odds, now)
            ? new ProMetric(100 * (1 - Math.Abs(2 * odds!.HomeWinProbability - 1)), oddsNote)
            : Missing("Game-winner odds missing, invalid, expired (one hour), or inapplicable to this schedule. " + oddsNote);
        var contextValid = evidence is not null && evidence.GameId == game.Id && evidence.Sport == game.Sport &&
            evidence.Season == game.SeasonYear && evidence.Home == game.HomeTeamAbbreviation && evidence.Away == game.AwayTeamAbbreviation &&
            evidence.ScheduledStart == game.ScheduledStart && (!game.SeasonType.HasValue || evidence.Phase == game.SeasonType) &&
            evidence.VerifiedUnstarted && !string.IsNullOrWhiteSpace(evidence.Source) && Fresh(evidence.ObservedAt, now, 1);
        var covered = (evidence?.CompletedGameIds ?? []).ToHashSet(StringComparer.Ordinal);
        var resultsCovered = knownCompletedIds.All(covered.Contains);
        var contextNote = evidence is null ? "No competition observation" : $"{evidence.Source}; season {evidence.Season}; observed {evidence.ObservedAt:O}. {evidence.Reason}";
        var nflLeverage = contextValid && resultsCovered ? NflPlayoffLeveragePolicy.Select(game, evidence, now, knownCompletedIds) : null;
        ProMetric stakes;
        if (!contextValid) stakes = Missing("Competition identity, schedule or phase missing/expired (one hour). " + contextNote);
        else if (evidence!.Phase == 1) stakes = new(0, "Verified preseason. " + contextNote);
        else if (evidence.Phase == 2 && home.IsFinal && away.IsFinal &&
            home.EvidenceKind == ProQualificationEvidenceKind.Kalshi && away.EvidenceKind == ProQualificationEvidenceKind.Kalshi &&
            home.Probability == 0 && away.Probability == 0 && home.ObservedAt.HasValue && home.ObservedAt <= now && away.ObservedAt.HasValue && away.ObservedAt <= now)
            stakes = new(0, $"Both teams confirmed eliminated; {home.Source} observed {home.ObservedAt:O}; {away.Source} observed {away.ObservedAt:O}. " + contextNote);
        else if (!resultsCovered) stakes = Missing("Competition context predates a known completed result. " + contextNote);
        else if (evidence.PlayIn is not null)
        {
            double? value = game.Sport == "nba" && evidence.Phase == 5 ? evidence.PlayIn switch { "second_chance" => 70, "knockout" => 75, "berth_decider" => 80, _ => null } : null;
            stakes = new(value, "NBA play-in advancement context. " + contextNote);
        }
        else if (evidence.Phase == 3)
        {
            double? basis = evidence.Round switch { 1 => 70, 2 => 75, 3 => 80, 4 => 85, _ => null };
            if (basis.HasValue && evidence.HomeElimination.HasValue && evidence.AwayElimination.HasValue)
            {
                var bonus = evidence.HomeElimination.Value && evidence.AwayElimination.Value ? 15 : evidence.HomeElimination.Value || evidence.AwayElimination.Value ? 10 : 0;
                stakes = new(basis + bonus, "Verified round and elimination context. " + contextNote);
            }
            else stakes = Missing("Postseason round or next-game elimination context unavailable. " + contextNote);
        }
        else if (evidence.Phase == 2)
        {
            StakesBounds Team(ProQualification qualification, ProTeamProgress progress, string team, NflConditionalPlayoffOdds? conditional)
            {
                var source = string.IsNullOrWhiteSpace(qualification.Source) ? "qualification evidence" : qualification.Source;
                var note = $"{team}: {source} observed {qualification.ObservedAt?.ToString("O") ?? "never"}. ";
                var trustedFinal = qualification.IsFinal && qualification.EvidenceKind == ProQualificationEvidenceKind.Kalshi &&
                    qualification.Probability is 0 or 1 && qualification.ObservedAt is DateTime finalAt && finalAt <= now;
                if (trustedFinal && qualification.Probability == 0) return new(0, 0, note + "Confirmed eliminated");
                if (trustedFinal && qualification.Probability == 1 && progress.SeedingUnresolved == false)
                    return new(0, 0, note + "Confirmed qualified with fully settled positioning");
                if (game.Sport == "nfl")
                {
                    if (!trustedFinal && conditional is null)
                        return new(null, null, $"{team}: PlayoffStatus next-game conditional playoff probabilities unavailable, invalid, or expired");
                    if (!trustedFinal) note = $"{team}: PlayoffStatus model; observed {nflLeverage!.ObservedAt:O}; published {conditional!.PublishedAt:O}. ";
                }
                else
                {
                    var selectedEstimateFresh = qualification.ExpiresAt is DateTime expiresAt && now < expiresAt;
                    if (!qualification.Probability.HasValue || !Probability(qualification.Probability.Value) ||
                        qualification.ObservedAt is not DateTime observed || observed > now ||
                        (qualification.IsFinal ? !trustedFinal : !selectedEstimateFresh))
                        return new(null, null, note + "Qualification probability unavailable, invalid, or expired");
                }
                if (progress.Scheduled is not int n || progress.Completed is not int g || n <= 0 || g < 0 || g >= n)
                    return new(null, null, note + "Verified season schedule/completed games unavailable or contradictory; " + progress.Reason);
                if (!progress.SeedingUnresolved.HasValue && !progress.ScheduleVerified)
                    return new(null, null, note + "Consistent team schedule not yet verified for the unknown-seeding bound; awaiting competition refresh");
                var urgency = (Math.Exp(3.0 * (g + 1) / n) - 1) / (Math.Exp(3) - 1);
                var (qualificationLower, qualificationUpper) = trustedFinal ? (0.0, 0.0) : game.Sport == "nfl"
                    ? NflPlayoffLeveragePolicy.Stakes(conditional!)
                    : (80 * urgency * (1 - Math.Abs(2 * qualification.Probability!.Value - 1)),
                       80 * urgency * (1 - Math.Abs(2 * qualification.Probability.Value - 1)));
                var seedingCeiling = 40 * urgency;
                note += $"{g}/{n} completed; {progress.Reason}";
                if (!progress.SeedingUnresolved.HasValue)
                    return new(qualificationLower, Math.Max(qualificationUpper, seedingCeiling),
                        note + $"; seeding remains unknown (qualification term {qualificationLower:G6}..{qualificationUpper:G6}, seeding ceiling {seedingCeiling:G6})", true);
                var seeding = progress.SeedingUnresolved.Value ? seedingCeiling : 0;
                return new(Math.Max(qualificationLower, seeding), Math.Max(qualificationUpper, seeding), note);
            }
            var h = Team(home, evidence.HomeProgress, game.HomeTeamAbbreviation, nflLeverage?.HomeOdds);
            var a = Team(away, evidence.AwayProgress, game.AwayTeamAbbreviation, nflLeverage?.AwayOdds);
            double? exact = null;
            if (h.Lower.HasValue && h.Upper.HasValue && a.Lower.HasValue && a.Upper.HasValue)
            {
                var lower = Math.Max(h.Lower.Value, a.Lower.Value);
                var upper = Math.Max(h.Upper.Value, a.Upper.Value);
                if (lower == upper) exact = lower;
            }
            var derivation = exact.HasValue && (h.UnknownSeeding || a.UnknownSeeding)
                ? "Exact Stakes: verified terms dominate all unknown seeding contributions; no seeding outcome assumed. " : "";
            stakes = new(exact, derivation + h.Explanation + "; " + a.Explanation + ". " + contextNote);
        }
        else stakes = Missing("Unsupported or unknown competition phase. " + contextNote);
        return new(stakes, quality, closeness, Composite(game.Sport, stakes.Value, quality.Value, closeness.Value), nflLeverage);
    }
}
