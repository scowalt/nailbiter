namespace backend.Models;

public static class PlayoffSeriesCatalogue
{
    private static readonly Dictionary<string, (int BestOf, string[] Sports)> Rounds = new(StringComparer.Ordinal)
    {
        ["ALWC"] = (3, ["mlb"]),
        ["NLWC"] = (3, ["mlb"]),
        ["ALDS"] = (5, ["mlb"]),
        ["NLDS"] = (5, ["mlb"]),
        ["ALCS"] = (7, ["mlb"]),
        ["NLCS"] = (7, ["mlb"]),
        ["World Series"] = (7, ["mlb"]),
        ["East 1st Round"] = (7, ["nba", "nhl"]),
        ["West 1st Round"] = (7, ["nba", "nhl"]),
        ["East Semifinals"] = (7, ["nba", "nhl"]),
        ["West Semifinals"] = (7, ["nba", "nhl"]),
        ["East Final"] = (7, ["nba", "nhl"]),
        ["West Final"] = (7, ["nba", "nhl"]),
        ["East Finals"] = (7, ["nba", "nhl"]),
        ["West Finals"] = (7, ["nba", "nhl"]),
        ["Eastern Conference Finals"] = (7, ["nba", "nhl"]),
        ["Western Conference Finals"] = (7, ["nba", "nhl"]),
        ["East 2nd Round"] = (7, ["nhl"]),
        ["West 2nd Round"] = (7, ["nhl"]),
        ["Stanley Cup Final"] = (7, ["nhl"]),
        ["NBA Finals"] = (7, ["nba"])
    };
    private static readonly HashSet<string> Sports = Rounds.Values.SelectMany(round => round.Sports).ToHashSet(StringComparer.Ordinal);

    public static bool Supports(string sport) => Sports.Contains(sport);
    public static bool RecognizesRound(string label) => Rounds.ContainsKey(label);
    public static bool RoundMatches(string sport, string? label, int bestOf) => label is null
        || (Rounds.TryGetValue(label, out var round) && round.BestOf == bestOf && round.Sports.Contains(sport, StringComparer.Ordinal));
}
