using System.Globalization;
using backend.Scoring;
using Microsoft.VisualBasic.FileIO;

namespace backend.Services;

public sealed record MoneyPuckRank(string Team, int Rank);
public sealed record MoneyPuckSnapshot(int Season, DateTime PublishedAt, IReadOnlyList<MoneyPuckRank> Teams);

public static class MoneyPuckRankings
{
    public const string Source = "moneypuck";
    public const string RankingsUrl = "https://moneypuck.com/moneypuck/powerRankings/gen2Model/rankings_current.csv";
    public const string UpdatedUrl = "https://moneypuck.com/moneypuck/powerRankings/gen2Model/update_time.txt";
    public const int MaxPublicationAgeHours = 7 * 24;
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly HashSet<string> Teams = [
        "ANA", "BOS", "BUF", "CAR", "CBJ", "CGY", "CHI", "COL", "DAL", "DET", "EDM", "FLA", "LAK", "MIN", "MTL", "NJD",
        "NSH", "NYI", "NYR", "OTT", "PHI", "PIT", "SEA", "SJS", "STL", "TBL", "TOR", "UTA", "VAN", "VGK", "WPG", "WSH"
    ];

    public static int SeasonAtPublication(DateTime publishedAt)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(publishedAt, Eastern);
        return local.Month >= 7 ? local.Year + 1 : local.Year;
    }

    public static bool EligiblePublication(DateTime publishedAt, DateTime now, int season) =>
        ProHypePolicy.Fresh(publishedAt, now, MaxPublicationAgeHours) && SeasonAtPublication(publishedAt) == season;

    public static MoneyPuckSnapshot? Parse(string csv, string updated, DateTime now)
    {
        if (!DateTime.TryParseExact(updated.Trim(), "dddd MMMM d, yyyy 'at' h:mm tt 'Eastern Time'",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var local) ||
            Eastern.IsInvalidTime(local) || Eastern.IsAmbiguousTime(local)) return null;
        var publishedAt = TimeZoneInfo.ConvertTimeToUtc(local, Eastern);
        var season = SeasonAtPublication(publishedAt);
        if (!EligiblePublication(publishedAt, now, season) || ProHypePolicy.TeamCount("nhl", season) != Teams.Count) return null;

        try
        {
            using var reader = new StringReader(csv);
            using var parser = new TextFieldParser(reader) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
            parser.SetDelimiters(",");
            var headers = parser.ReadFields();
            if (headers is null || headers.Distinct().Count() != headers.Length) return null;
            var teamColumn = Array.IndexOf(headers, "teamCode");
            var scoreColumn = Array.IndexOf(headers, "avg_overall_prediction");
            if (teamColumn < 0 || scoreColumn < 0) return null;
            var seen = new HashSet<string>();
            var ranks = new List<MoneyPuckRank>();
            while (!parser.EndOfData)
            {
                var row = parser.ReadFields();
                if (row is null || row.Length != headers.Length || !Teams.Contains(row[teamColumn]) || !seen.Add(row[teamColumn]) ||
                    !double.TryParse(row[scoreColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out var score) ||
                    !ProHypePolicy.Probability(score)) return null;
                var team = row[teamColumn] switch { "LAK" => "LA", "NJD" => "NJ", "SJS" => "SJ", "TBL" => "TB", "UTA" => "UTAH", var code => code };
                ranks.Add(new(team, ranks.Count + 1));
            }
            return seen.SetEquals(Teams) ? new(season, publishedAt, ranks) : null;
        }
        catch (MalformedLineException) { return null; }
    }
}
