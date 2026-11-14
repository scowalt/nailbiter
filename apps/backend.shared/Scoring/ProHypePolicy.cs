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
    private sealed record StakesBounds(double? Lower, double? Upper, string Explanation);
    private const double MaxStakesSpread = 1;

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

    private static bool ValidRankFacts(TeamRating r, string sport, int season) =>
        r.Sport == sport && r.Season == season && r.SourceSeason == season &&
        TeamCount(sport, season) is int n && r.PowerRank is int rank && rank >= 1 && rank <= n &&
        (r.Source != MoneyPuckRankings.Source || (sport == "nhl" && r.RankPublishedAt is DateTime published && published <= r.FetchedAt &&
            MoneyPuckRankings.SeasonAtPublication(published) == season)) &&
        (r.Source != NhlPowerRankingsGuruService.Source || (sport == "nhl" && r.RankPublishedAt is DateTime guruPublished
            && NhlPowerRankingsGuruService.ValidPublication(guruPublished, r.FetchedAt, season)));

    public static bool EligibleRank(TeamRating r, string sport, int season, DateTime now) =>
        ValidRankFacts(r, sport, season) && Fresh(r.FetchedAt, now, 48) &&
        (r.Source != MoneyPuckRankings.Source || MoneyPuckRankings.EligiblePublication(r.RankPublishedAt!.Value, now, season)) &&
        (r.Source != NhlPowerRankingsGuruService.Source || Fresh(r.RankPublishedAt!.Value, now, NhlPowerRankingsGuruService.MaxPublicationAgeHours));

    private static bool ContextApplicable(Game game, ProGameEvidence? context) => context is not null
        && context.GameId == game.Id && context.Sport == game.Sport && context.Season == game.SeasonYear
        && (!game.SeasonType.HasValue || context.Phase == game.SeasonType) && context.Home == game.HomeTeamAbbreviation
        && context.Away == game.AwayTeamAbbreviation && context.ScheduledStart == game.ScheduledStart
        && context.VerifiedUnstarted && context.Rejection == ProCompetitionRejection.None && !string.IsNullOrWhiteSpace(context.Source);

    private static string CompetitionRevision(ProGameEvidence context) => context.Phase != 2
        ? ProEvidenceReference.Fingerprint(context)
        : ProEvidenceReference.Fingerprint(new
        {
            context.GameId, context.Sport, context.Season, context.Home, context.Away, context.ScheduledStart,
            context.ObservedAt, context.Source, context.VerifiedUnstarted, context.Phase, context.Round,
            context.HomeElimination, context.AwayElimination, context.PlayIn,
            HomeProgress = new { context.HomeProgress?.Completed, context.HomeProgress?.Scheduled, context.HomeProgress?.ScheduleVerified },
            AwayProgress = new { context.AwayProgress?.Completed, context.AwayProgress?.Scheduled, context.AwayProgress?.ScheduleVerified },
            context.NflPlayoffLeverage, context.CompletedGameIds, context.Reason, context.Rejection
        });

    private sealed record RankingPair(string Source, TeamRating? Home, TeamRating? Away)
    {
        public IEnumerable<TeamRating> Rows => new[] { Home, Away }.OfType<TeamRating>();
    }

    private static RankingPair[] RankingCandidates(Game game, IEnumerable<TeamRating> ratings)
    {
        var rows = ratings.ToArray();
        var sources = game.Sport switch
        {
            "nfl" => new[] { "espn_fpi", "powerrankingsguru" },
            "nba" => new[] { "espn_bpi", "powerrankingsguru" },
            "nhl" => new[] { MoneyPuckRankings.Source, NhlPowerRankingsGuruService.Source },
            _ => new[] { "powerrankingsguru" }
        };
        TeamRating? Latest(string source, string team) => rows.Where(r => r.Sport == game.Sport && r.Season == game.SeasonYear
            && r.Source == source && TeamKey(game.Sport, r.TeamAbbreviation) == TeamKey(game.Sport, team))
            .OrderByDescending(r => r.FetchedAt).ThenByDescending(r => r.Id).FirstOrDefault();
        return sources.Select(source => new RankingPair(source, Latest(source, game.HomeTeamAbbreviation), Latest(source, game.AwayTeamAbbreviation))).ToArray();
    }

    private static RankingPair? SelectRankingPair(Game game, RankingPair[] candidates, DateTime now) =>
        candidates.FirstOrDefault(pair => pair.Home is not null && pair.Away is not null
            && EligibleRank(pair.Home, game.Sport, game.SeasonYear.GetValueOrDefault(), now)
            && EligibleRank(pair.Away, game.Sport, game.SeasonYear.GetValueOrDefault(), now)
            && (pair.Source != NhlPowerRankingsGuruService.Source || pair.Home.RankPublishedAt == pair.Away.RankPublishedAt
                && pair.Home.FetchedAt == pair.Away.FetchedAt));

    public static ProMetric Quality(Game game, IEnumerable<TeamRating> ratings, DateTime now)
    {
        if (game.SeasonYear is not int season || TeamCount(game.Sport, season) is not int n)
            return new(null, "Verified season and league team count unavailable");
        var candidates = RankingCandidates(game, ratings);
        var selected = SelectRankingPair(game, candidates, now);
        string Publication(TeamRating row) => row.Source == MoneyPuckRankings.Source
            ? $"; rolling ranking published {row.RankPublishedAt?.ToString("O") ?? "unknown"} (seven-day limit; season from Eastern publication date)"
            : row.Source == NhlPowerRankingsGuruService.Source ? $"; Power Rankings Guru published {row.RankPublishedAt:O} (48-hour limit)" : "";
        var observations = new List<string>();
        foreach (var pair in candidates)
        {
            var source = pair.Source;
            foreach (var row in pair.Rows)
                observations.Add($"{source} {row.TeamAbbreviation}, source season {row.SourceSeason?.ToString() ?? "unverified"}, observed {row.FetchedAt:O}{Publication(row)}");
            if (pair != selected) continue;
            var home = pair.Home!;
            var away = pair.Away!;
            double Strength(int rank) => 100.0 * (n - rank + 0.5) / n;
            var h = Strength(home.PowerRank!.Value);
            var a = Strength(away.PowerRank!.Value);
            return new(2 * h * a / (h + a), $"{source}: #{home.PowerRank} / #{away.PowerRank}; season {season}, {n} teams; observed {home.FetchedAt:O} / {away.FetchedAt:O}{Publication(home)}{(home.RankPublishedAt != away.RankPublishedAt ? Publication(away) : "")}");
        }
        return new(null, "No complete current-season same-source ranking pair fetched within 48 hours. " +
            (game.Sport == "nhl" ? "MoneyPuck requires a publication less than seven days old; Power Rankings Guru requires a coherent batch published within 48 hours. " : "") + string.Join("; ", observations));
    }

    internal static ProHypeAssessment Assess(Game game, ProGameEvidence? context, TeamRating[] ratings,
        ProHypeResult result, ProEvidenceReference[] qualifications, ProQualification home, ProQualification away,
        PregameOdds? odds, IEnumerable<string> completed, DateTime now)
    {
        var observations = new List<ProEvidenceReference>(qualifications);
        var support = new List<ProEvidenceReference>();
        var contextApplicable = ContextApplicable(game, context);
        if (context is not null)
        {
            var reference = new ProEvidenceReference(ProEvidenceKind.Competition, context.Source, "", CompetitionRevision(context),
                context.ObservedAt, null, !contextApplicable || context.ObservedAt > now ? ProEvidenceState.Rejected
                    : Fresh(context.ObservedAt, now, 1) ? ProEvidenceState.Accepted : ProEvidenceState.Gap,
                !contextApplicable ? "inapplicable_context" : "competition", []);
            observations.Add(reference);
            if (result.Stakes.Value.HasValue) support.Add(reference);
        }
        var candidates = RankingCandidates(game, ratings);
        var selected = SelectRankingPair(game, candidates, now);
        foreach (var pair in candidates)
        {
            var references = pair.Rows.Select(r => new ProEvidenceReference(ProEvidenceKind.Ranking, r.Source, r.TeamAbbreviation,
                ProEvidenceReference.Fingerprint(new { r.FetchedAt, r.SourceSeason, r.PowerRank, r.RankPublishedAt }), r.FetchedAt, r.RankPublishedAt,
                EligibleRank(r, game.Sport, game.SeasonYear.GetValueOrDefault(), now) ? ProEvidenceState.Accepted
                    : r.FetchedAt <= now && ValidRankFacts(r, game.Sport, game.SeasonYear.GetValueOrDefault()) ? ProEvidenceState.Gap : ProEvidenceState.Rejected,
                "ranking", [], PowerRank: r.PowerRank)).ToArray();
            observations.AddRange(references);
            if (pair == selected) support.AddRange(references);
        }
        if (odds is not null)
        {
            var reference = new ProEvidenceReference(ProEvidenceKind.Odds, odds.Source, "", ProEvidenceReference.Fingerprint(new { odds.FetchedAt, odds.Source,
                odds.SourceSeason, odds.ApplicableStart, odds.HomeTeamAbbreviation, odds.AwayTeamAbbreviation,
                odds.HomeWinProbability, odds.AwayWinProbability, odds.DrawWinProbability }), odds.FetchedAt, null,
                EligibleOdds(game, odds, now) ? ProEvidenceState.Accepted : odds.FetchedAt <= now && EligibleOdds(game, odds, odds.FetchedAt)
                    ? ProEvidenceState.Gap : ProEvidenceState.Rejected, "game_winner", []);
            observations.Add(reference);
            if (result.Closeness.Value.HasValue) support.Add(reference);
        }
        if (context?.Phase == 2)
        {
            bool Final(ProQualification value) => value.IsFinal && value.EvidenceKind == ProQualificationEvidenceKind.Kalshi;
            if (result.Stakes.Value.HasValue)
                support.AddRange(qualifications.Where(q => q.State == ProEvidenceState.Accepted &&
                    ((TeamKey(game.Sport, q.Team) == TeamKey(game.Sport, game.HomeTeamAbbreviation) && q.Source == home.Source && (game.Sport != "nfl" || Final(home)))
                    || (TeamKey(game.Sport, q.Team) == TeamKey(game.Sport, game.AwayTeamAbbreviation) && q.Source == away.Source && (game.Sport != "nfl" || Final(away))))));
            if (game.Sport == "nfl")
            {
                var evidence = context.NflPlayoffLeverage;
                var current = NflPlayoffLeveragePolicy.Select(game, context, now, completed);
                var original = evidence is null ? null : NflPlayoffLeveragePolicy.Select(game,
                    new ProGameEvidence { NflPlayoffLeverage = evidence, CompletedGameIds = evidence.CompletedGameIds }, evidence.ObservedAt);
                foreach (var (team, qualification, scored, usable, valid) in new[] {
                    (game.HomeTeamAbbreviation, home, result.NflPlayoffLeverage?.HomeOdds, current?.HomeOdds, original?.HomeOdds),
                    (game.AwayTeamAbbreviation, away, result.NflPlayoffLeverage?.AwayOdds, current?.AwayOdds, original?.AwayOdds) })
                {
                    var reference = ProEvidenceReference.Conditional(evidence, team, context.ObservedAt,
                        usable is not null ? ProEvidenceState.Accepted : valid is not null ? ProEvidenceState.Gap : ProEvidenceState.Rejected);
                    observations.Add(reference);
                    if (result.Stakes.Value.HasValue && (!Final(qualification)
                        || qualification.Probability == 1 && NflPlayoffLeveragePolicy.Positions(scored) is not null)) support.Add(reference);
                }
            }
        }
        var problems = new List<ProHypeProblem>();
        if (!Supports(game.Sport) || game.IsHistoricalImport || game.Status != "scheduled" || game.ActualStart.HasValue
            || game.ScheduledStart <= now || !game.SeasonYear.HasValue) problems.Add(ProHypeProblem.InvalidGame);
        if (!result.Total.HasValue)
        {
            if (observations.Any(r => r.State == ProEvidenceState.Rejected)) problems.Add(ProHypeProblem.InvalidEvidence);
            if (contextApplicable && !completed.All((context!.CompletedGameIds ?? []).Contains)) problems.Add(ProHypeProblem.CompletedResultLag);
            if (problems.Count == 0 || observations.Any(r => r.State == ProEvidenceState.Gap)) problems.Add(ProHypeProblem.EvidenceGap);
        }
        return new(problems.ToArray(), support.ToArray(), observations.ToArray());
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
            "nhl" => (.55, .30, .15),
            "nba" => (.45, .25, .30),
            "nfl" => (.45, .35, .20),
            "mlb" => (.35, .30, .35),
            _ => throw new ArgumentException("Unsupported pro league", nameof(sport))
        };
        return s * stakes + q * quality + c * closeness * quality / 100;
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
        var contextValid = ContextApplicable(game, evidence) && Fresh(evidence!.ObservedAt, now, 1);
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
                if (trustedFinal && qualification.Probability == 1 && game.Sport == "nfl" && NflPlayoffLeveragePolicy.Positions(conditional) is not null)
                {
                    var (seedingLower, seedingUpper) = NflPlayoffLeveragePolicy.Stakes(conditional!, clinched: true);
                    return new(seedingLower, seedingUpper, note + "Confirmed qualified; playoff-position Stakes from PlayoffStatus division title and No. 1 seed scenarios");
                }
                if (trustedFinal)
                    return new(0, 0, note + (qualification.Probability == 0 ? "Confirmed eliminated" : "Confirmed qualified")
                        + "; qualification-only regular-season Stakes");
                if (game.Sport == "nfl")
                {
                    if (conditional is null)
                        return new(null, null, $"{team}: PlayoffStatus next-game conditional playoff probabilities unavailable, invalid, or expired");
                    note = $"{team}: PlayoffStatus model; observed {nflLeverage!.ObservedAt:O}; published {conditional.PublishedAt:O}. ";
                }
                else
                {
                    var selectedEstimateFresh = qualification.ExpiresAt is DateTime expiresAt && now < expiresAt;
                    if (!qualification.Probability.HasValue || !Probability(qualification.Probability.Value) ||
                        qualification.ObservedAt is not DateTime observed || observed > now ||
                        qualification.IsFinal || !selectedEstimateFresh)
                        return new(null, null, note + "Qualification probability unavailable, invalid, or expired");
                }
                if (progress.Scheduled is not int n || progress.Completed is not int g || n <= 0 || g < 0 || g >= n)
                    return new(null, null, note + "Verified season schedule/completed games unavailable or contradictory");
                if (!progress.ScheduleVerified)
                    return new(null, null, note + "Consistent team schedule not yet verified; awaiting competition refresh");
                if (game.Sport == "nfl")
                {
                    var (lower, upper) = NflPlayoffLeveragePolicy.Stakes(conditional!, clinched: false);
                    return new(lower, upper, note + $"{g}/{n} completed; " + (NflPlayoffLeveragePolicy.Positions(conditional) is null
                        ? "qualification-only regular-season Stakes; seed scenarios unavailable"
                        : "playoff-position Stakes from qualification, division title and No. 1 seed scenarios"));
                }
                var estimate = 80 * PlayoffSwingEstimate.Swing(qualification.Probability!.Value, n - g);
                return new(estimate, estimate, note + $"{g}/{n} completed; qualification-only regular-season Stakes from estimated playoff swing");
            }
            var h = Team(home, evidence.HomeProgress, game.HomeTeamAbbreviation, nflLeverage?.HomeOdds);
            var a = Team(away, evidence.AwayProgress, game.AwayTeamAbbreviation, nflLeverage?.AwayOdds);
            double? value = null;
            if (h.Lower.HasValue && h.Upper.HasValue && a.Lower.HasValue && a.Upper.HasValue)
            {
                var lower = Math.Max(h.Lower.Value, a.Lower.Value);
                var upper = Math.Max(h.Upper.Value, a.Upper.Value);
                if (upper - lower <= MaxStakesSpread) value = (lower + upper) / 2;
            }
            stakes = new(value, h.Explanation + "; " + a.Explanation + ". " + contextNote);
        }
        else stakes = Missing("Unsupported or unknown competition phase. " + contextNote);
        return new(stakes, quality, closeness, Composite(game.Sport, stakes.Value, quality.Value, closeness.Value), nflLeverage);
    }
}
