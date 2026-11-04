using System.Text.Json;
using backend.Models;
using backend.Services;

namespace backend.tests;

internal sealed class NhlSeedingScenario
{
    internal static readonly DateTime Now = new(2026, 10, 11, 18, 0, 0, DateTimeKind.Utc);
    internal sealed record Contest(string Id, string Home, string Away);
    internal Contest[] Contests { get; }
    internal string[] Teams { get; }
    internal NhlSeedingScenario()
    {
        Contests = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ProHype", "nhl-seeding-contests.csv"))
            .Skip(1).Select(line => line.Split(',')).Select(row => new Contest(row[0], row[1], row[2])).ToArray();
        Teams = Contests.SelectMany(c => new[] { c.Home, c.Away }).Distinct().ToArray();
    }
    internal Contest Target => Contests.First(c => c.Home == "ANA" && c.Away == "LA");
    internal string TeamId(string team) => (Array.IndexOf(Teams, team) + 1001).ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal Game Game => new()
    {
        Id = Target.Id, Sport = "nhl", HomeTeam = "Synthetic ANA", AwayTeam = "Synthetic LA",
        HomeTeamAbbreviation = Target.Home, AwayTeamAbbreviation = Target.Away,
        HomeTeamId = TeamId(Target.Home), AwayTeamId = TeamId(Target.Away),
        SeasonYear = 2027, SeasonType = 2, ScheduledStart = Now.AddHours(4)
    };
    internal JsonElement Event(Contest c) => JsonSerializer.SerializeToElement(new
    {
        id = c.Id, date = Game.ScheduledStart, season = new { year = 2027, type = 2 }, seasonType = new { type = 2 },
        competitions = new[] { new {
            id = c.Id, date = Game.ScheduledStart,
            status = new { period = 0, type = new { name = "STATUS_SCHEDULED", state = "pre", completed = false } },
            competitors = new[] { new { id = TeamId(c.Home), homeAway = "home", score = "0", team = new { id = TeamId(c.Home), abbreviation = c.Home } },
                new { id = TeamId(c.Away), homeAway = "away", score = "0", team = new { id = TeamId(c.Away), abbreviation = c.Away } } }
        } }
    });
    internal JsonElement ScheduleJson(string team, Func<Contest, JsonElement>? createEvent = null) => JsonSerializer.SerializeToElement(new
    {
        status = "success", timestamp = Now, season = new { year = 2027, type = 2 },
        team = new { id = TeamId(team), abbreviation = team, groups = new { id = "synthetic" } },
        events = Contests.Where(c => c.Home == team || c.Away == team).Select(createEvent ?? Event).ToArray()
    });
    internal ProCompetitionEvidenceService.Schedule[] Schedules(Func<Contest, JsonElement>? createEvent = null) => Teams.Select(team =>
        ProCompetitionEvidenceService.ParseSchedule(ScheduleJson(team, createEvent), "nhl", 2027, 2, team, Now))
        .OfType<ProCompetitionEvidenceService.Schedule>().ToArray();
    internal ProGameEvidence Evidence(ProCompetitionEvidenceService.Schedule[]? schedules = null) =>
        ProCompetitionEvidenceService.ParseEvent(Game, Event(Target), schedules ?? Schedules(), Now, "independently generated synthetic season");
    internal Dictionary<string, int> IndependentCompletion(string targetWinner)
    {
        var points = Teams.ToDictionary(team => team, _ => 0);
        var extraLoss = Contests.First(c => c.Home == "ANA" && c.Away == "BOS").Id;
        foreach (var c in Contests)
        {
            var winner = c.Id == Target.Id ? targetWinner : c.Home == "ANA" && c.Away == "LA" ? "ANA"
                : c.Id == extraLoss ? "BOS" : c.Home == "ANA" || c.Away == "ANA" ? "ANA"
                : c.Home == "LA" || c.Away == "LA" ? "LA" : c.Home;
            if (winner != c.Home && winner != c.Away) throw new InvalidOperationException("Invalid synthetic winner");
            points[winner] += 2;
        }
        return points;
    }
}
