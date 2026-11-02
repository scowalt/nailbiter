using System.Text.Json;
using System.Text.RegularExpressions;
using backend.Data;
using backend.Models;
using backend.Scoring;
using backend.Services.Espn;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public sealed class ProCompetitionEvidenceService(HttpClient http, ExcitementDbContext db,
    TimeProvider clock, ILogger<ProCompetitionEvidenceService> logger, NflPlayoffLeverageCollector? nflLeverage = null)
{
    public static string PathFor(string sport) => sport switch
    {
        "nfl" => "football/nfl",
        "nba" => "basketball/nba",
        "nhl" => "hockey/nhl",
        "mlb" => "baseball/mlb",
        _ => throw new ArgumentException("Unsupported league", nameof(sport))
    };
    private static JsonElement Get(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    private static string? Text(JsonElement e, string name) => Get(e, name).ValueKind == JsonValueKind.String ? Get(e, name).GetString() : null;
    private static int? Number(JsonElement e, string name) => Get(e, name).ValueKind == JsonValueKind.Number && Get(e, name).TryGetInt32(out var n) ? n : null;
    private static bool IsTrue(JsonElement e, string name) => Get(e, name).ValueKind == JsonValueKind.True;
    private static JsonElement[] Items(JsonElement e, string name) => Get(e, name).ValueKind == JsonValueKind.Array ? Get(e, name).EnumerateArray().ToArray() : [];
    private static DateTime? Date(JsonElement e, string name) => DateTimeOffset.TryParse(Text(e, name), out var date) ? date.UtcDateTime : null;
    private static string? Abbreviation(JsonElement competitor)
    {
        var value = Text(Get(competitor, "team"), "abbreviation");
        return value == "UTA" ? "UTAH" : value;
    }
    private static bool Final(JsonElement competition) => Text(Get(Get(competition, "status"), "type"), "state") == "post" &&
        IsTrue(Get(Get(competition, "status"), "type"), "completed") &&
        Text(Get(Get(competition, "status"), "type"), "name") is "STATUS_FINAL" or "STATUS_FINAL_OVERTIME" or "STATUS_FINAL_SO";

    private static bool Unstarted(string sport, JsonElement competition) =>
        EspnPregameStatus.IsUnstarted(sport,
            Text(Get(Get(competition, "status"), "type"), "name"),
            Text(Get(Get(competition, "status"), "type"), "state"),
            IsTrue(Get(Get(competition, "status"), "type"), "completed"), Number(Get(competition, "status"), "period")) &&
        Items(competition, "competitors").All(x => Get(x, "score").ValueKind == JsonValueKind.Undefined ||
            Get(x, "score").ToString() == "0" || Get(Get(x, "score"), "value").ToString() == "0");

    public sealed record Schedule(string Team, string? Group, int Season, int Phase, int Length, int Completed,
        double Points, string[] CompletedIds, JsonElement[] Events);

    public static Schedule? ParseSchedule(JsonElement root, string sport, int season, int phase, string team, DateTime now)
    {
        try { return ParseScheduleCore(root, sport, season, phase, team, now); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException) { return null; }
    }

    private static Schedule? ParseScheduleCore(JsonElement root, string sport, int season, int phase, string team, DateTime now)
    {
        if (Text(root, "status") != "success" || Number(Get(root, "season"), "year") != season || Number(Get(root, "season"), "type") != phase ||
            Text(Get(root, "team"), "abbreviation") != team || Date(root, "timestamp") is not DateTime stamp || !ProHypePolicy.Fresh(stamp, now, 1)) return null;
        var events = Items(root, "events");
        if (events.Any(e => Number(Get(e, "season"), "year") != season || Number(Get(e, "seasonType"), "type") != phase || string.IsNullOrEmpty(Text(e, "id")))) return null;
        if (events.GroupBy(e => Text(e, "id")).Any(g => g.Count() > 1)) return null;
        if (phase == 2 && (ProHypePolicy.ScheduledLength(sport, season) is not int expected || events.Length != expected)) return null;
        var completed = new List<string>();
        double points = 0;
        var canceled = 0;
        foreach (var e in events)
        {
            var competitions = Items(e, "competitions");
            if (competitions.Length != 1) return null;
            var c = competitions[0];
            var competitors = Items(c, "competitors");
            if (competitors.Length != 2 || competitors.Count(x => Abbreviation(x) == team) != 1) return null;
            var status = Get(Get(c, "status"), "type");
            var name = Text(status, "name");
            if (name == "STATUS_CANCELED") { canceled++; continue; }
            if (!Final(c))
            {
                if (name is not ("STATUS_SCHEDULED" or "STATUS_POSTPONED" or "STATUS_SUSPENDED" or "STATUS_IN_PROGRESS" or "STATUS_DELAYED" or "STATUS_END_PERIOD" or "STATUS_HALFTIME")) return null;
                continue;
            }
            if (Date(e, "date") is not DateTime start || start > now) return null;
            var own = competitors.Single(x => Abbreviation(x) == team);
            var opponent = competitors.Single(x => Abbreviation(x) != team);
            double? Score(JsonElement x)
            {
                var s = Get(x, "score");
                if (s.ValueKind == JsonValueKind.Object) s = Get(s, "value");
                return double.TryParse(s.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
            }
            var ownScore = Score(own); var otherScore = Score(opponent);
            if (!ownScore.HasValue || !otherScore.HasValue || ownScore < 0 || otherScore < 0 || (sport != "nfl" && ownScore == otherScore)) return null;
            if (sport == "nhl")
            {
                if (ownScore == otherScore || Number(Get(c, "status"), "period") is not int period || period < 3) return null;
                points += ownScore > otherScore ? 2 : period > 3 ? 1 : 0;
            }
            else points += ownScore > otherScore ? 1 : ownScore == otherScore && sport == "nfl" ? 0.5 : 0;
            completed.Add(Text(e, "id")!);
        }
        return new(team, Text(Get(Get(root, "team"), "groups"), "id"), season, phase, events.Length - canceled, completed.Count, points, completed.ToArray(), events);
    }

    public static void ApplySeedingCertificates(ProGameEvidence evidence, IReadOnlyCollection<Schedule> league)
    {
        var n = ProHypePolicy.TeamCount(evidence.Sport, evidence.Season);
        var length = ProHypePolicy.ScheduledLength(evidence.Sport, evidence.Season);
        if (!n.HasValue || league.Count != n || league.Select(s => s.Team).Distinct().Count() != n ||
            league.Any(s => s.Season != evidence.Season || s.Phase != 2 || s.Length != length)) return;
        var games = league.SelectMany(s => s.Events.Select(e => (Team: s.Team, Event: e))).GroupBy(x => Text(x.Event, "id"));
        if (games.Any(g => g.Count() != 2 || g.Select(x => x.Team).Distinct().Count() != 2 ||
            g.Select(x => Final(Items(x.Event, "competitions")[0])).Distinct().Count() != 1 ||
            g.Select(x => ResultFingerprint(x.Event)).Distinct().Count() != 1)) return;
        double Award = evidence.Sport == "nhl" ? 2 : 1;
        double Max(Schedule s) => s.Points + Award * (s.Length - s.Completed);
        void Apply(string team, ProTeamProgress target)
        {
            var own = league.SingleOrDefault(s => s.Team == ProHypePolicy.TeamKey(evidence.Sport, team));
            if (own is null) return;
            if (league.Where(s => s.Team != own.Team).All(other => own.Points > Max(other) || Max(own) < other.Points))
            {
                target.SeedingUnresolved = false;
                target.Reason = "Strict full-league final-record bounds fix all relative positions, including home advantage";
            }
        }
        Apply(evidence.Home, evidence.HomeProgress); Apply(evidence.Away, evidence.AwayProgress);
        var h = league.SingleOrDefault(s => s.Team == ProHypePolicy.TeamKey(evidence.Sport, evidence.Home));
        var a = league.SingleOrDefault(s => s.Team == ProHypePolicy.TeamKey(evidence.Sport, evidence.Away));
        if (h is null || a is null || !h.Events.Any(e => Text(e, "id") == evidence.GameId) ||
            !a.Events.Any(e => Text(e, "id") == evidence.GameId) ||
            (evidence.Sport == "nfl" && (h.Group is null || h.Group != a.Group))) return;
        int RemainingAgainst(Schedule s, string opponent) => s.Events.Count(e =>
            !Final(Items(e, "competitions")[0]) && Items(Items(e, "competitions")[0], "competitors").Any(t => Abbreviation(t) == opponent));
        var meetings = RemainingAgainst(h, a.Team);
        if (meetings < 1 || meetings != RemainingAgainst(a, h.Team)) return;
        var hBase = h.Points + Award * (h.Length - h.Completed - meetings);
        var aBase = a.Points + Award * (a.Length - a.Completed - meetings);
        var k = Math.Round(((meetings - 1) - (hBase - aBase) / Award) / 2);
        var lowerFinal = Math.Min(hBase + Award * k, aBase + Award * (meetings - 1 - k));
        var bothAboveOthers = league.Where(s => s.Team != h.Team && s.Team != a.Team).All(s =>
            s.Points + Award * (s.Length - s.Completed - RemainingAgainst(s, h.Team) - RemainingAgainst(s, a.Team)) < lowerFinal);
        if (bothAboveOthers && k >= 0 && k <= meetings - 1 && Math.Abs(hBase - aBase + Award * (2 * k - (meetings - 1))) < Award)
        {
            evidence.HomeProgress.SeedingUnresolved = evidence.AwayProgress.SeedingUnresolved = true;
            evidence.HomeProgress.Reason = evidence.AwayProgress.Reason = "Complete-schedule strict two-team record bounds prove this result can exchange first/second positioning or home advantage while both teams qualify; no tiebreaker assumption";
        }
    }

    private static string ResultFingerprint(JsonElement e)
    {
        var c = Items(e, "competitions")[0];
        return $"{Date(e, "date"):O}|{Text(Get(Get(c, "status"), "type"), "name")}|{Number(Get(c, "status"), "period")}|" +
            string.Join(";", Items(c, "competitors").OrderBy(Abbreviation).Select(t => $"{Abbreviation(t)}:{Get(t, "score")}"));
    }

    public static ProGameEvidence ParseEvent(Game game, JsonElement e, IReadOnlyCollection<Schedule> schedules, DateTime observedAt, string source)
    {
        try { return ParseEventCore(game, e, schedules, observedAt, source); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
        {
            return new()
            {
                GameId = game.Id,
                Sport = game.Sport,
                Season = game.SeasonYear ?? 0,
                ObservedAt = observedAt,
                Source = source,
                Reason = "Successful observation has ambiguous or malformed competition context"
            };
        }
    }

    private static ProGameEvidence ParseEventCore(Game game, JsonElement e, IReadOnlyCollection<Schedule> schedules, DateTime observedAt, string source)
    {
        var result = new ProGameEvidence
        {
            GameId = game.Id,
            Sport = game.Sport,
            Home = game.HomeTeamAbbreviation,
            Away = game.AwayTeamAbbreviation,
            Season = Number(Get(e, "season"), "year") ?? 0,
            Phase = Number(Get(e, "season"), "type"),
            ScheduledStart = Date(e, "date") ?? default,
            ObservedAt = observedAt,
            Source = source,
            Reason = "ESPN event identity and schedule observation"
        };
        var c = Items(e, "competitions").SingleOrDefault();
        var competitors = Items(c, "competitors");
        if (Text(e, "id") != game.Id || result.Season != game.SeasonYear || result.ScheduledStart != game.ScheduledStart || competitors.Length != 2 ||
            !competitors.Any(x => Text(x, "homeAway") == "home" && Abbreviation(x) == ProHypePolicy.TeamKey(game.Sport, result.Home)) ||
            !competitors.Any(x => Text(x, "homeAway") == "away" && Abbreviation(x) == ProHypePolicy.TeamKey(game.Sport, result.Away)) ||
            competitors.Any(x => !int.TryParse(Text(x, "id"), out _) || Abbreviation(x) is null or "TBD")) return result;
        result.VerifiedUnstarted = Unstarted(game.Sport, c);
        var eligible = schedules.Where(s => s.Season == result.Season && s.Phase == result.Phase).ToArray();
        result.CompletedGameIds = eligible.SelectMany(s => s.CompletedIds).Distinct().ToArray();
        if (result.Phase == 2)
        {
            var conflictingIds = eligible.SelectMany(s => s.Events).GroupBy(x => Text(x, "id"))
                .Where(g => g.Select(x => (Final(Items(x, "competitions")[0]), ResultFingerprint(x))).Distinct().Count() > 1)
                .Select(g => g.Key).ToHashSet();
            foreach (var (team, progress) in new[] { (result.Home, result.HomeProgress), (result.Away, result.AwayProgress) })
            {
                var schedule = eligible.SingleOrDefault(s => s.Team == ProHypePolicy.TeamKey(game.Sport, team));
                if (schedule is null) continue;
                var scheduledEvent = schedule.Events.SingleOrDefault(x => Text(x, "id") == game.Id);
                if (scheduledEvent.ValueKind == JsonValueKind.Undefined) continue;
                var scheduledCompetition = Items(scheduledEvent, "competitions").Single();
                var scheduledTeams = Items(scheduledCompetition, "competitors");
                if (Date(scheduledEvent, "date") != result.ScheduledStart || !Unstarted(game.Sport, scheduledCompetition) ||
                    !scheduledTeams.Any(x => Text(x, "homeAway") == "home" && Abbreviation(x) == ProHypePolicy.TeamKey(game.Sport, result.Home)) ||
                    !scheduledTeams.Any(x => Text(x, "homeAway") == "away" && Abbreviation(x) == ProHypePolicy.TeamKey(game.Sport, result.Away)))
                {
                    result.VerifiedUnstarted = false;
                    result.Reason += "; Team schedule contradicts the upcoming event identity, start or status";
                    continue;
                }
                if (schedule.Events.Any(x => conflictingIds.Contains(Text(x, "id"))))
                {
                    progress.Reason = "Conflicting schedule observations prevent verified completed-game progress";
                    continue;
                }
                progress.Scheduled = schedule.Length;
                progress.Completed = schedule.Completed;
                progress.ScheduleVerified = true;
            }
            if (result.VerifiedUnstarted) ApplySeedingCertificates(result, eligible);
        }
        var label = string.Join("; ", Items(c, "notes").Select(x => Text(x, "headline")));
        if (result.Phase == 5 && game.Sport == "nba")
        {
            result.PlayIn = label switch
            {
                "NBA Play-In - East - 7th Place vs 8th Place" or "NBA Play-In - West - 7th Place vs 8th Place" => "second_chance",
                "NBA Play-In - East - 9th Place vs 10th Place" or "NBA Play-In - West - 9th Place vs 10th Place" => "knockout",
                "NBA Play-In - East - 8th Seed Game" or "NBA Play-In - West - 8th Seed Game" => "berth_decider",
                _ => null
            };
        }
        if (result.Phase == 3)
        {
            var roundLabel = Regex.Replace(label, @" - Game [1-7]$", "");
            result.Round = (game.Sport, roundLabel) switch
            {
                ("nhl", "Stanley Cup Final") or ("nba", "NBA Finals") or ("mlb", "World Series") => 4,
                ("nhl", "East Final" or "West Final") or ("nba", "East Finals" or "West Finals") or ("nhl" or "nba", "Eastern Conference Finals" or "Western Conference Finals") or ("mlb", "ALCS" or "NLCS") or ("nfl", "AFC Championship" or "NFC Championship") => 3,
                ("nhl", "East 2nd Round" or "West 2nd Round" or "Eastern Conference Second Round" or "Western Conference Second Round") or ("nba", "East Semifinals" or "West Semifinals" or "Eastern Conference Semifinals" or "Western Conference Semifinals") or ("mlb", "ALDS" or "NLDS") or ("nfl", "AFC Divisional Playoffs" or "NFC Divisional Playoffs") => 2,
                ("nhl" or "nba", "East 1st Round" or "West 1st Round" or "Eastern Conference First Round" or "Western Conference First Round") or ("mlb", "ALWC" or "NLWC" or "AL Wild Card" or "NL Wild Card") or ("nfl", "AFC Wild Card Playoffs" or "NFC Wild Card Playoffs") => 1,
                _ => null
            };
            if (game.Sport == "nfl" && Regex.IsMatch(label, @"^Super Bowl [IVXLCDM]+$")) result.Round = 4;
            if (game.Sport == "nfl" && result.Round.HasValue) result.HomeElimination = result.AwayElimination = true;
            else
            {
                var series = Get(c, "series");
                var members = Items(series, "competitors");
                var ordinal = Regex.Match(label, @" - Game ([1-7])$");
                var length = Number(series, "totalCompetitions");
                if (Text(series, "type") == "playoff" && !IsTrue(series, "completed") && members.Length == 2 && length is 3 or 5 or 7 && ordinal.Success)
                {
                    var homeId = Text(competitors.Single(x => Text(x, "homeAway") == "home"), "id");
                    var awayId = Text(competitors.Single(x => Text(x, "homeAway") == "away"), "id");
                    var homeWins = Number(members.SingleOrDefault(x => Text(x, "id") == homeId), "wins");
                    var awayWins = Number(members.SingleOrDefault(x => Text(x, "id") == awayId), "wins");
                    var needed = length / 2 + 1;
                    if (homeWins >= 0 && awayWins >= 0 && homeWins < needed && awayWins < needed &&
                        homeWins + awayWins + 1 == int.Parse(ordinal.Groups[1].Value))
                    {
                        var homeSchedule = eligible.SingleOrDefault(s => s.Team == ProHypePolicy.TeamKey(game.Sport, result.Home));
                        var prior = homeSchedule?.Events.Where(x => Date(x, "date") < game.ScheduledStart &&
                            Items(Items(x, "competitions").Single(), "competitors").Any(t => Abbreviation(t) == ProHypePolicy.TeamKey(game.Sport, result.Away))).ToArray();
                        if (prior is not null && homeSchedule!.Events.Any(x => Text(x, "id") == game.Id) && prior.Length == homeWins + awayWins && prior.All(x => Final(Items(x, "competitions").Single())))
                        {
                            int Wins(string team) => prior.Count(x => Items(Items(x, "competitions").Single(), "competitors").Any(t => Abbreviation(t) == ProHypePolicy.TeamKey(game.Sport, team) && IsTrue(t, "winner")));
                            if (Wins(result.Home) == homeWins && Wins(result.Away) == awayWins)
                            { result.HomeElimination = awayWins == needed - 1; result.AwayElimination = homeWins == needed - 1; }
                        }
                    }
                }
            }
        }
        result.Reason += "; " + label;
        return result;
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var horizon = now.AddDays(7);
        var games = await db.Games.Where(g => g.Status == "scheduled" && !g.IsHistoricalImport && g.ScheduledStart > now && g.ScheduledStart < horizon &&
            (g.Sport == "nfl" || g.Sport == "nba" || g.Sport == "nhl" || g.Sport == "mlb")).ToListAsync(ct);
        var cache = new Dictionary<string, JsonElement>();
        async Task<JsonElement> Fetch(string url)
        {
            if (cache.TryGetValue(url, out var value)) return value;
            var body = await http.GetStringAsync(url, ct);
            try
            {
                using var document = JsonDocument.Parse(body);
                return cache[url] = document.RootElement.Clone();
            }
            catch (JsonException) { return cache[url] = default; }
        }
        foreach (var group in games.Where(g => g.SeasonYear.HasValue).GroupBy(g => (g.Sport, Season: g.SeasonYear!.Value, Phase: g.SeasonType)))
        {
            try
            {
                var baseUrl = EspnSiteApi.url("/apis/site/v2/sports/" + PathFor(group.Key.Sport));
                var schedules = new List<Schedule>();
                var league = default(JsonElement);
                if (group.Key.Phase != 1)
                {
                    var teamsDoc = await Fetch($"{baseUrl}/teams?limit=1000&season={group.Key.Season}");
                    league = Items(Items(teamsDoc, "sports").SingleOrDefault(), "leagues").SingleOrDefault();
                }
                if (Number(Get(league, "season"), "year") == group.Key.Season)
                {
                    foreach (var entry in Items(league, "teams"))
                    {
                        var team = Get(entry, "team");
                        var id = Text(team, "id"); var abbr = Text(team, "abbreviation");
                        if (id is null || abbr is null) continue;
                        var phase = group.Key.Phase ?? 2;
                        var response = await Fetch($"{baseUrl}/teams/{id}/schedule?season={group.Key.Season}&seasontype={phase}");
                        var parsed = ParseSchedule(response, group.Key.Sport, group.Key.Season, phase, abbr, clock.GetUtcNow().UtcDateTime);
                        if (parsed is not null) schedules.Add(parsed);
                    }
                }
                var contexts = new List<(Game Game, ProGameEvidence Evidence)>();
                foreach (var game in group)
                {
                    var easternDate = TimeZoneInfo.ConvertTimeFromUtc(game.ScheduledStart, TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
                    var url = $"{baseUrl}/scoreboard?dates={easternDate:yyyyMMdd}&limit=1000";
                    var board = await Fetch(url);
                    var e = Items(board, "events").SingleOrDefault(x => Text(x, "id") == game.Id);
                    var evidence = ParseEvent(game, e, schedules, now, url + "; ESPN season team schedules");
                    if (game.Sport == "nfl" && evidence.Phase == 2)
                        evidence.NflPlayoffLeverage = ProGameEvidence.Parse(game.ProEvidenceJson)?.NflPlayoffLeverage;
                    contexts.Add((game, evidence));
                }
                if (group.Key.Sport == "nfl" && nflLeverage is not null)
                {
                    var forecasts = await nflLeverage.CollectAsync(contexts.Where(x => x.Evidence.VerifiedUnstarted && x.Evidence.Phase == 2)
                        .Select(x => x.Game).ToArray(), schedules, ct);
                    foreach (var (game, evidence) in contexts)
                        if (forecasts.TryGetValue(game.Id, out var forecast) && forecast is not null) evidence.NflPlayoffLeverage = forecast;
                }
                foreach (var (game, evidence) in contexts) game.ProEvidenceJson = JsonSerializer.Serialize(evidence);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Pro context request failed for {Sport}/{Season}; previous timestamps preserved", group.Key.Sport, group.Key.Season);
            }
        }
    }
}

public sealed class ProCompetitionRefreshBackgroundService(IServiceScopeFactory scopes,
    ILogger<ProCompetitionRefreshBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ProCompetitionEvidenceService>().RefreshAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogError(ex, "Pro competition refresh failed"); }
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }
}
