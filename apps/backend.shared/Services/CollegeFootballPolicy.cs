using System.Text.RegularExpressions;
using backend.Models;

namespace backend.Services;

public static class CollegeFootballPolicy
{
    public const string HistoricalSource = "Kalshi historical 1m trade closes";
    public static int SeasonAt(DateTime date) => date.Month >= 2 ? date.Year : date.Year - 1;

    public static string WeekLabel(int? seasonType, int week) => seasonType switch
    {
        3 => "Postseason (Bowls & CFP)",
        1 => $"Preseason Week {week}",
        2 when week == 1 => "Week 1 (includes Week 0)",
        _ => $"Week {week}"
    };

    public static string KalshiCode(string abbreviation) => abbreviation.ToUpperInvariant() switch
    {
        "CCU" => "CCAR", "IU" => "IND", "TA&M" => "TXAM", "OU" => "OKLA", "BOIS" => "BSU",
        "MIZ" => "MIZZ", "SC" => "SCAR", "NCSU" => "NCST", "M-OH" => "MOH",
        _ => abbreviation.ToUpperInvariant()
    };

    public static string Classify(int seasonType, string evidence, string? competitionType = null)
    {
        if (Regex.IsMatch(evidence, @"\b(CFP|College Football Playoff)\s+(First Round|Quarterfinal|Semifinal|National Championship|Championship)\b", RegexOptions.IgnoreCase)) return "cfp";
        if (Regex.IsMatch(evidence,
            @"\b(ACC|SEC|Big Ten|Big 10|Big 12|Big Twelve|AAC|American|Conference USA|C-USA|MAC|Mountain West|Sun Belt|Pac-12|Pac-10)\b.*\bChampionship\b",
            RegexOptions.IgnoreCase)) return "conference_championship";
        if (seasonType == 3 && competitionType == "42" && evidence.Contains("Bowl", StringComparison.OrdinalIgnoreCase)
            && !Regex.IsMatch(evidence, @"\b(CFP|College Football Playoff|Rose|Sugar|Cotton|Orange|Peach|Fiesta)\b", RegexOptions.IgnoreCase)) return "bowl";
        return seasonType == 2 ? "regular" : "unknown";
    }

    public static double? Quality(double? home, double? away)
    {
        if (home is not double h || away is not double a || !double.IsFinite(h) || !double.IsFinite(a)) return null;
        h = 1 / (1 + Math.Exp(-h / 15));
        a = 1 / (1 + Math.Exp(-a / 15));
        return 200 * h * a / (h + a);
    }

    public static double? Stakes(Game game, double? homeQualification, double? awayQualification)
    {
        if (game.CollegeCompetition == "cfp") return 100;
        if (game.CollegeCompetition == "conference_championship") return 80;
        if (game.CollegeCompetition == "bowl") return 0;
        if (CfpRelevance(game, homeQualification, awayQualification) is not double relevance) return null;
        var year = game.SeasonYear ?? SeasonAt(game.ScheduledStart);
        var progress = Math.Clamp((game.ScheduledStart - new DateTime(year, 8, 22)).TotalDays / 106, 0, 1);
        var urgency = (Math.Exp(3 * progress) - 1) / (Math.Exp(3) - 1);
        var h = homeQualification!.Value;
        var a = awayQualification!.Value;
        return Math.Clamp((40 * Bubble(h, a) + 30 * Math.Min(h, a)) * urgency + 20 * progress * relevance, 0, 90);
    }

    public static double? CfpRelevance(Game game, double? homeQualification, double? awayQualification)
    {
        if (game.CollegeCompetition is "cfp" or "conference_championship" or "bowl") return 1;
        if (game.CollegeCompetition != "regular" || !Probability(homeQualification) || !Probability(awayQualification)) return null;
        var h = homeQualification!.Value;
        var a = awayQualification!.Value;
        return Math.Max(Bubble(h, a), Math.Min(h, a));
    }

    private static double Bubble(double h, double a) => ((1 - Math.Abs(h - .5) * 2) + (1 - Math.Abs(a - .5) * 2)) / 2;

    public static bool Probability(double? value) => value is double p && double.IsFinite(p) && p >= 0 && p <= 1;
    public static bool Fresh(DateTime? timestamp, DateTime now, TimeSpan age) =>
        timestamp is DateTime t && t <= now && now - t < age;
    public static bool UsablePregameOdds(Game game, PregameOdds? odds, DateTime now)
    {
        if (game.Sport != "cfb" || game.IsHistoricalImport || game.Status != "scheduled" || game.ActualStart.HasValue
            || odds is null || odds.GameId != game.Id || odds.DrawWinProbability.HasValue
            || !Probability(odds.HomeWinProbability) || !Probability(odds.AwayWinProbability)
            || Math.Abs(odds.HomeWinProbability + odds.AwayWinProbability - 1) > 1e-8) return false;
        if (odds.Source is "Kalshi" or GameWinnerProbabilityPolicy.OneSidedSource)
            return Fresh(odds.FetchedAt, now, TimeSpan.FromMinutes(2))
                && odds.SourceSeason == game.SeasonYear && odds.SourceSeason.HasValue
                && odds.ApplicableStart == game.ScheduledStart
                && odds.HomeTeamAbbreviation == game.HomeTeamAbbreviation
                && odds.AwayTeamAbbreviation == game.AwayTeamAbbreviation;
        return odds.Source.StartsWith("ESPN ", StringComparison.Ordinal)
            && odds.HomeMoneyline is int home && Math.Abs((long)home) >= 100
            && odds.AwayMoneyline is int away && Math.Abs((long)away) >= 100
            && Fresh(odds.FetchedAt, now, TimeSpan.FromHours(24));
    }

    public static bool UsableLiveOdds(Game game, OddsSnapshot snapshot, DateTime now) =>
        !snapshot.IsHistorical && Fresh(snapshot.Timestamp, now, TimeSpan.FromMinutes(2)) &&
        (game.MarketObservedAt is null || snapshot.Timestamp >= game.MarketObservedAt);

    public static string? Logo(string? espnId) => espnId is not null && Regex.IsMatch(espnId, @"^\d+$")
        ? $"https://a.espncdn.com/i/teamlogos/ncaa/500/{espnId}.png" : null;
    public static string Color(string? color) => color is not null && Regex.IsMatch(color, @"^#?[0-9a-fA-F]{6}$")
        ? "#" + color.TrimStart('#') : "#6b7280";
}
