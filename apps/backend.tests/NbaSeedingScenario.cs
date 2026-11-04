using System.Text.Json;
using System.Text.Json.Nodes;
using backend.Models;
using backend.Services;

namespace backend.tests;

internal sealed class NbaSeedingScenario
{
    internal static readonly DateTime Now = new(2026, 11, 16, 15, 0, 0, DateTimeKind.Utc);
    internal sealed record Contest(string Id, string Home, string Away);
    internal Contest[] Contests { get; }
    internal string[] Teams { get; }
    internal NbaSeedingScenario()
    {
        Contests = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ProHype", "nba-seeding-contests.csv"))
            .Skip(1).Select(line => line.Split(',')).Select(row => new Contest(row[0], row[1], row[2])).ToArray();
        Teams = Contests.SelectMany(c => new[] { c.Home, c.Away }).Distinct().ToArray();
    }
    internal Contest Target => Contests.First(c => c.Home == "BOS" && c.Away == "MIA");
    internal string TeamId(string team) => (Array.IndexOf(Teams, team) + 2001).ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal Game Game => new()
    {
        Id = Target.Id, Sport = "nba", HomeTeam = "Synthetic BOS", AwayTeam = "Synthetic MIA",
        HomeTeamAbbreviation = Target.Home, AwayTeamAbbreviation = Target.Away,
        HomeTeamId = TeamId(Target.Home), AwayTeamId = TeamId(Target.Away),
        SeasonYear = 2027, SeasonType = 2, ScheduledStart = Now.AddHours(3)
    };
    internal JsonElement Event(Contest c) => JsonSerializer.SerializeToElement(new
    {
        id = c.Id, date = c.Id == Target.Id ? Game.ScheduledStart : new DateTime(2027, 2, 1, 20, 0, 0, DateTimeKind.Utc),
        season = new { year = 2027, type = 2 }, seasonType = new { type = 2 },
        competitions = new[] { new {
            id = c.Id, type = new { abbreviation = "STD" }, notes = Array.Empty<object>(),
            status = new { period = 0, type = new { name = "STATUS_SCHEDULED", state = "pre", completed = false } },
            competitors = new[] { new { id = TeamId(c.Home), homeAway = "home", score = "0", team = new { id = TeamId(c.Home), abbreviation = c.Home } },
                new { id = TeamId(c.Away), homeAway = "away", score = "0", team = new { id = TeamId(c.Away), abbreviation = c.Away } } }
        } }
    });
    internal JsonElement ScheduleJson(string team, Func<Contest, JsonElement>? createEvent = null)
    {
        var events = Contests.Where(c => c.Home == team || c.Away == team).Select(createEvent ?? Event).ToArray();
        var completed = events.Where(e => e.GetProperty("competitions")[0].GetProperty("status").GetProperty("type").GetProperty("completed").GetBoolean()).ToArray();
        var wins = completed.Count(e =>
        {
            var competitors = e.GetProperty("competitions")[0].GetProperty("competitors").EnumerateArray().ToArray();
            var homeScore = double.Parse(competitors[0].GetProperty("score").ToString(), System.Globalization.CultureInfo.InvariantCulture);
            var awayScore = double.Parse(competitors[1].GetProperty("score").ToString(), System.Globalization.CultureInfo.InvariantCulture);
            return (competitors[0].GetProperty("team").GetProperty("abbreviation").GetString() == team) == (homeScore > awayScore);
        });
        return JsonSerializer.SerializeToElement(new
        {
            status = "success", timestamp = Now, season = new { year = 2027, type = 2 },
            team = new { id = TeamId(team), abbreviation = team, seasonSummary = "2026-27", recordSummary = $"{wins}-{completed.Length - wins}" }, events
        });
    }
    internal JsonElement EarlyEvent(Contest contest)
    {
        var homeFinals = Contests.Where(c => c.Id != Target.Id && (c.Home == "BOS" || c.Away == "BOS") && c.Home != "MIA" && c.Away != "MIA").Take(7).ToArray();
        var awayFinals = Contests.Where(c => c.Id != Target.Id && (c.Home == "MIA" || c.Away == "MIA") && c.Home != "BOS" && c.Away != "BOS").Take(8).ToArray();
        var own = homeFinals.Contains(contest) ? "BOS" : awayFinals.Contains(contest) ? "MIA" : null;
        if (own is null) return Event(contest);
        var index = Array.IndexOf(own == "BOS" ? homeFinals : awayFinals, contest);
        var ownWins = index % 2 == 1;
        return Change(contest, e =>
        {
            e["date"] = JsonValue.Create(Now.AddDays(-2));
            var c = e["competitions"]![0]!;
            c["status"] = JsonSerializer.SerializeToNode(new { period = 4, type = new { name = "STATUS_FINAL", state = "post", completed = true } });
            c["competitors"]![0]!["score"] = (contest.Home == own) == ownWins ? "110" : "100";
            c["competitors"]![1]!["score"] = (contest.Home == own) == ownWins ? "100" : "110";
        });
    }
    internal Dictionary<string, int> IndependentCompletion(int homeExtra, int awayExtra, string targetWinner)
    {
        var winners = Contests.ToDictionary(c => c.Id, c => c.Id == Target.Id ? targetWinner
            : c.Home == "BOS" || c.Away == "BOS" ? "BOS" : c.Home == "MIA" || c.Away == "MIA" ? "MIA"
            : System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(c.Id))[0] % 2 == 0 ? c.Home : c.Away);
        var difference = 1 + homeExtra - awayExtra;
        var leader = difference > 0 ? "BOS" : "MIA";
        foreach (var contest in Contests.Where(c => (c.Home == "ATL" && c.Away == leader) || (c.Home == leader && c.Away == "ATL")).Take(Math.Abs(difference)))
            winners[contest.Id] = "ATL";
        var points = Teams.ToDictionary(team => team, _ => 0);
        foreach (var contest in Contests)
        {
            var winner = winners[contest.Id];
            if (winner != contest.Home && winner != contest.Away) throw new InvalidOperationException("Invalid synthetic winner");
            points[winner]++;
        }
        points["BOS"] += homeExtra; points["MIA"] += awayExtra;
        return points;
    }
    internal ProCompetitionEvidenceService.Schedule[] Schedules(Func<Contest, JsonElement>? createEvent = null) => Teams.Select(team =>
        ProCompetitionEvidenceService.ParseSchedule(ScheduleJson(team, createEvent), "nba", 2027, 2, team, Now))
        .OfType<ProCompetitionEvidenceService.Schedule>().ToArray();
    internal ProGameEvidence Evidence(ProCompetitionEvidenceService.Schedule[]? schedules = null, JsonElement? target = null) =>
        ProCompetitionEvidenceService.ParseEvent(Game, target ?? Event(Target), schedules ?? Schedules(), Now, "independently generated synthetic NBA season");
    internal JsonElement Change(Contest contest, Action<JsonObject> change)
    {
        var value = JsonNode.Parse(Event(contest).GetRawText())!.AsObject();
        change(value);
        return JsonSerializer.SerializeToElement(value);
    }
}
