using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using backend.Models;
using backend.Scoring;

namespace backend.Services;

public sealed class NflPlayoffLeverageCollector(HttpClient http, TimeProvider clock, ILogger<NflPlayoffLeverageCollector> logger)
{
    private sealed record Team(string Slug, string Abbreviation, string Name);
    private sealed record Page(string Html, DateTime? PublishedAt, bool ClockValid);
    private sealed record Contest(string Home, string Away, DateOnly Date, DateTime? Start, int Week, int? HomeScore, int? AwayScore)
    {
        public string Key => $"{Home}/{Away}/{Date:yyyy-MM-dd}";
    }
    private sealed record League(int Season, string Label, int Week, Contest[] Games);
    private sealed class DeniedException : HttpRequestException;
    private const string ResultPending = "PlayoffStatus has not yet published a completed NFL result";
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly Team[] Teams =
    [
        new("cardinals", "ARI", "Arizona Cardinals"), new("falcons", "ATL", "Atlanta Falcons"),
        new("ravens", "BAL", "Baltimore Ravens"), new("bills", "BUF", "Buffalo Bills"),
        new("panthers", "CAR", "Carolina Panthers"), new("bears", "CHI", "Chicago Bears"),
        new("bengals", "CIN", "Cincinnati Bengals"), new("browns", "CLE", "Cleveland Browns"),
        new("cowboys", "DAL", "Dallas Cowboys"), new("broncos", "DEN", "Denver Broncos"),
        new("lions", "DET", "Detroit Lions"), new("packers", "GB", "Green Bay Packers"),
        new("texans", "HOU", "Houston Texans"), new("colts", "IND", "Indianapolis Colts"),
        new("jaguars", "JAX", "Jacksonville Jaguars"), new("chiefs", "KC", "Kansas City Chiefs"),
        new("raiders", "LV", "Las Vegas Raiders"), new("chargers", "LAC", "Los Angeles Chargers"),
        new("rams", "LAR", "Los Angeles Rams"), new("dolphins", "MIA", "Miami Dolphins"),
        new("vikings", "MIN", "Minnesota Vikings"), new("patriots", "NE", "New England Patriots"),
        new("saints", "NO", "New Orleans Saints"), new("giants", "NYG", "New York Giants"),
        new("jets", "NYJ", "New York Jets"), new("eagles", "PHI", "Philadelphia Eagles"),
        new("steelers", "PIT", "Pittsburgh Steelers"), new("forty-niners", "SF", "San Francisco 49ers"),
        new("seahawks", "SEA", "Seattle Seahawks"), new("buccaneers", "TB", "Tampa Bay Buccaneers"),
        new("titans", "TEN", "Tennessee Titans"), new("commanders", "WSH", "Washington Commanders")
    ];
    private static string Canonical(string abbreviation) => abbreviation switch { "LA" => "LAR", "WAS" => "WSH", _ => abbreviation };
    private static string Text(INode? node) => Regex.Replace(node?.TextContent ?? "", @"\s+", " ").Trim();
    private static bool Complete(Page page) => page.Html.TrimEnd().EndsWith("</html>", StringComparison.OrdinalIgnoreCase) && page.ClockValid;
    private static string Label(IDocument doc) => Text(doc.QuerySelector("header .datetime"));
    private static int Week(IDocument doc)
    {
        var match = Regex.Match(Text(doc.QuerySelector(".wkinfo")), @"^NFL - Week ([1-9]|1[0-8]) of 18$");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    private async Task<Page> FetchAsync(string url, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new DeniedException();
        if ((int)response.StatusCode >= 500) response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > 1024 * 1024)
            return new("", null, false);
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(block, deadline.Token)) > 0)
        {
            if (buffer.Length + read > 1024 * 1024) return new("", null, false);
            buffer.Write(block, 0, read);
        }
        var now = clock.GetUtcNow().UtcDateTime;
        var published = response.Content.Headers.LastModified?.UtcDateTime;
        var serverDate = response.Headers.Date?.UtcDateTime;
        var valid = published.HasValue && NflPlayoffLeveragePolicy.Published(published.Value, now, now) &&
            serverDate.HasValue && ProHypePolicy.Fresh(serverDate.Value, now, 1);
        try { return new(new UTF8Encoding(false, true).GetString(buffer.ToArray()), published, valid); }
        catch (DecoderFallbackException) { return new("", published, false); }
    }

    private static (DateOnly Date, DateTime? Start)? Date(string value, int season)
    {
        var match = Regex.Match(value, @"^(Mon|Tue|Wed|Thu|Fri|Sat|Sun) (Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) (\d{1,2}), (TBD|\d{1,2}:\d{2} [ap]m)$");
        if (!match.Success) return null;
        var year = match.Groups[2].Value is "Jan" or "Feb" ? season + 1 : season;
        if (!DateOnly.TryParseExact($"{match.Groups[2].Value} {match.Groups[3].Value} {year}", "MMM d yyyy", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) || date.ToString("ddd", CultureInfo.InvariantCulture) != match.Groups[1].Value) return null;
        if (match.Groups[4].Value == "TBD") return (date, null);
        if (!DateTime.TryParseExact($"{date:yyyy-MM-dd} {match.Groups[4].Value}", "yyyy-MM-dd h:mm tt", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var local) || Eastern.IsAmbiguousTime(local) || Eastern.IsInvalidTime(local)) return null;
        return (date, TimeZoneInfo.ConvertTimeToUtc(local, Eastern));
    }

    private static League? ParseLeague(Page page, int season)
    {
        if (!Complete(page)) return null;
        using var doc = new HtmlParser().ParseDocument(page.Html);
        if (Text(doc.QuerySelector("h1")) != $"{season} NFL Schedule" || Week(doc) == 0 || Label(doc) == "") return null;
        var tables = doc.QuerySelectorAll(".mncntnt table").Where(t =>
            t.QuerySelectorAll("tr").FirstOrDefault()?.QuerySelectorAll("th").Select(Text)
                .SequenceEqual(new[] { "Home Team", "Score", "Visiting Team", "Date / Time (ET)" }) == true).ToArray();
        if (tables.Length != 18) return null;
        var games = new List<Contest>();
        for (var i = 0; i < tables.Length; i++)
        {
            var preceding = tables[i].PreviousElementSibling;
            while (preceding is not null && preceding.Id == "" && preceding.LocalName != "table") preceding = preceding.PreviousElementSibling;
            if (preceding?.Id != (i + 1).ToString(CultureInfo.InvariantCulture)) return null;
            foreach (var row in tables[i].QuerySelectorAll("tr").Skip(1))
            {
                var cells = row.QuerySelectorAll("td").ToArray();
                if (cells.Length != 4) return null;
                Team? TeamAt(int column) => Teams.SingleOrDefault(t => cells[column].QuerySelector("a")?.GetAttribute("href") == "#" + t.Slug);
                var home = TeamAt(0); var away = TeamAt(2); var date = Date(Text(cells[3]), season);
                if (home is null || away is null || home == away || date is null) return null;
                int? hs = null, @as = null;
                var score = Text(cells[1]);
                if (score != "")
                {
                    var match = Regex.Match(score, @"^(\d{1,3})[\-\u2011\u2013](\d{1,3})(?: \(OT\))?$");
                    if (!match.Success || date.Value.Start is null) return null;
                    hs = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    @as = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                }
                games.Add(new(home.Abbreviation, away.Abbreviation, date.Value.Date, date.Value.Start, i + 1, hs, @as));
            }
        }
        if (games.Count != 272 || games.Select(g => g.Key).Distinct().Count() != games.Count ||
            Teams.Any(t => games.Count(g => g.Home == t.Abbreviation || g.Away == t.Abbreviation) != 17)) return null;
        return new(season, Label(doc), Week(doc), games.ToArray());
    }

    private static string? VerifyResults(League source, IReadOnlyCollection<ProCompetitionEvidenceService.Schedule> schedules)
    {
        if (schedules.Count != 32 || schedules.Select(s => Canonical(s.Team)).Distinct().Count() != 32 ||
            schedules.Any(s => s.Season != source.Season || s.Phase != 2 || s.Length != 17 || s.Events.Length != 17 ||
                s.Completed != s.CompletedIds.Length || !Teams.Any(t => t.Abbreviation == Canonical(s.Team))))
            return "Complete verified NFL team schedules unavailable";
        var actual = new List<(string Id, Contest Contest)>();
        foreach (var group in schedules.SelectMany(s => s.Events.Select(e => (Schedule: s, Event: e))).GroupBy(x => x.Event.GetProperty("id").GetString()))
        {
            if (string.IsNullOrEmpty(group.Key) || group.Count() != 2 || group.Select(x => Canonical(x.Schedule.Team)).Distinct().Count() != 2)
                return "Ambiguous NFL schedule copies";
            Contest? previous = null;
            foreach (var (schedule, e) in group)
            {
                var competition = e.GetProperty("competitions").EnumerateArray().Single();
                var teams = competition.GetProperty("competitors").EnumerateArray().ToArray();
                var home = teams.Single(t => t.GetProperty("homeAway").GetString() == "home");
                var away = teams.Single(t => t.GetProperty("homeAway").GetString() == "away");
                var start = DateTimeOffset.Parse(e.GetProperty("date").GetString()!, CultureInfo.InvariantCulture).UtcDateTime;
                var final = schedule.CompletedIds.Contains(group.Key);
                int Score(System.Text.Json.JsonElement team) => ProCompetitionEvidenceService.ReadScore(team)
                    ?? throw new FormatException("NFL result score must be a nonnegative integer");
                var game = new Contest(Canonical(home.GetProperty("team").GetProperty("abbreviation").GetString()!),
                    Canonical(away.GetProperty("team").GetProperty("abbreviation").GetString()!),
                    DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(start, Eastern)), start,
                    e.TryGetProperty("week", out var week) && week.TryGetProperty("number", out var number) ? number.GetInt32() : 0,
                    final ? Score(home) : null, final ? Score(away) : null);
                if (previous is not null && previous != game) return "Contradictory NFL schedule copies";
                previous = game;
            }
            actual.Add((group.Key!, previous!));
        }
        if (actual.Count != 272) return "Incomplete NFL league schedule";
        foreach (var game in actual)
        {
            var matches = source.Games.Where(x => x.Home == game.Contest.Home && x.Away == game.Contest.Away).ToArray();
            if (matches.Length != 1 || (game.Contest.Week != 0 && matches[0].Week != game.Contest.Week) ||
                (game.Contest.Week == 0 && matches[0].Date != game.Contest.Date))
                return "PlayoffStatus future schedule does not match the verified NFL season";
        }
        var pending = false;
        foreach (var game in actual.Where(x => x.Contest.HomeScore.HasValue))
        {
            var matches = source.Games.Where(x => x.Key == game.Contest.Key).ToArray();
            if (matches.Length == 1 && matches[0].Start == game.Contest.Start && !matches[0].HomeScore.HasValue) { pending = true; continue; }
            if (matches.Length != 1 || matches[0].Start != game.Contest.Start || matches[0].HomeScore != game.Contest.HomeScore || matches[0].AwayScore != game.Contest.AwayScore)
                return "PlayoffStatus schedule contradicts a completed NFL result";
        }
        foreach (var game in source.Games.Where(x => x.HomeScore.HasValue))
            if (actual.Count(x => x.Contest.Key == game.Key && x.Contest.Start == game.Start && x.Contest.HomeScore == game.HomeScore && x.Contest.AwayScore == game.AwayScore) != 1)
                return "PlayoffStatus completed result not corroborated by NFL schedules";
        return pending ? ResultPending : null;
    }

    private static NflCorroboratedKickoff? CorroborateKickoff(Game game, Contest source,
        IReadOnlyCollection<ProCompetitionEvidenceService.Schedule> schedules)
    {
        if (source.Start is not DateTime sourceStart || source.Date !=
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(game.ScheduledStart, Eastern))) return null;
        string? Confirm(string team)
        {
            var schedule = schedules.Single(s => Canonical(s.Team) == Canonical(team));
            var target = schedule.Events.SingleOrDefault(e => e.GetProperty("id").GetString() == game.Id);
            if (target.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !target.TryGetProperty("timeValid", out var valid) || valid.ValueKind != System.Text.Json.JsonValueKind.True ||
                target.GetProperty("season").GetProperty("year").GetInt32() != game.SeasonYear ||
                target.GetProperty("seasonType").GetProperty("type").GetInt32() != 2 ||
                target.GetProperty("week").GetProperty("number").GetInt32() != game.WeekNumber ||
                DateTimeOffset.Parse(target.GetProperty("date").GetString()!, CultureInfo.InvariantCulture).UtcDateTime != game.ScheduledStart)
                return null;
            var competition = target.GetProperty("competitions").EnumerateArray().Single();
            var status = competition.GetProperty("status");
            var state = status.GetProperty("type");
            var teams = competition.GetProperty("competitors").EnumerateArray().ToArray();
            if (!EspnPregameStatus.IsUnstarted("nfl", state.GetProperty("name").GetString(), state.GetProperty("state").GetString(),
                state.GetProperty("completed").GetBoolean(), status.GetProperty("period").GetInt32()) ||
                teams.Any(t => t.TryGetProperty("score", out _) && ProCompetitionEvidenceService.ReadScore(t) != 0) ||
                Canonical(teams.Single(t => t.GetProperty("homeAway").GetString() == "home").GetProperty("team").GetProperty("abbreviation").GetString()!) != Canonical(game.HomeTeamAbbreviation) ||
                Canonical(teams.Single(t => t.GetProperty("homeAway").GetString() == "away").GetProperty("team").GetProperty("abbreviation").GetString()!) != Canonical(game.AwayTeamAbbreviation)) return null;
            var next = schedule.Events.Where(e => !schedule.CompletedIds.Contains(e.GetProperty("id").GetString()!))
                .OrderBy(e => DateTimeOffset.Parse(e.GetProperty("date").GetString()!, CultureInfo.InvariantCulture)).FirstOrDefault();
            return next.ValueKind == System.Text.Json.JsonValueKind.Object && next.GetProperty("id").GetString() == game.Id ? game.Id : null;
        }
        if (Confirm(game.HomeTeamAbbreviation) is not string home || Confirm(game.AwayTeamAbbreviation) is not string away) return null;
        var match = new NflCorroboratedKickoff
        {
            Week = game.WeekNumber.GetValueOrDefault(), SourceScheduledStart = sourceStart, EspnScheduledStart = game.ScheduledStart,
            HomeGameId = home, AwayGameId = away, HomeNextGameId = home, AwayNextGameId = away
        };
        return NflPlayoffLeveragePolicy.ValidCorroboratedKickoff(game, match) ? match : null;
    }

    private static (double Lower, double Upper)? Probability(string value)
    {
        if (value == "<1%") return (0, .01);
        if (value == ">99%") return (.99, 1);
        if (!Regex.IsMatch(value, @"^\d{1,3}(?:\.\d+)?%$") ||
            !double.TryParse(value[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) || number > 100) return null;
        return (number / 100, number / 100);
    }

    private static NflConditionalPlayoffOdds? ParseTeam(Page page, Team team, Team opponent, League league, string url)
    {
        if (!Complete(page)) return null;
        using var doc = new HtmlParser().ParseDocument(page.Html);
        var title = Text(doc.QuerySelector("title"));
        var identified = title == team.Name + " What If?" || team.Abbreviation == "SF" && title == "San Francisco Forty-Niners What If?";
        if (!identified || Label(doc) != league.Label || Week(doc) != league.Week) return null;
        var headings = doc.QuerySelectorAll(".mncntnt h3").Where(h => Text(h).StartsWith("Next Game - ", StringComparison.Ordinal)).ToArray();
        if (headings.Length != 1) return null;
        var label = Regex.Replace(Text(headings[0]), @"^Next Game - | \([\d\u2011\-]+\)$", "").Replace("Forty-Niners49ers", "Forty-Niners");
        if (!label.Equals(opponent.Slug, StringComparison.OrdinalIgnoreCase) && !(opponent.Abbreviation == "SF" && label == "49ers")) return null;
        var table = headings[0].NextElementSibling;
        if (table?.LocalName != "table") return null;
        var rows = table.QuerySelectorAll("tr").ToArray();
        if (rows.Length != 5 || !Text(rows[0].QuerySelectorAll("th").LastOrDefault()).Contains("No Playoffs", StringComparison.Ordinal)) return null;
        var data = new Dictionary<string, string[]>();
        foreach (var row in rows.Skip(2))
        {
            var cells = row.QuerySelectorAll("td").Select(Text).ToArray();
            if (cells.Length != 12 || !data.TryAdd(cells[0], cells)) return null;
        }
        if (!data.TryGetValue("Win Next Game", out var win) || !data.TryGetValue("Current Standings", out var current) || !data.TryGetValue("Lose Next Game", out var loss)) return null;
        var completed = league.Games.Where(g => g.HomeScore.HasValue && (g.Home == team.Abbreviation || g.Away == team.Abbreviation)).ToArray();
        var wins = completed.Count(g => g.Home == team.Abbreviation ? g.HomeScore > g.AwayScore : g.AwayScore > g.HomeScore);
        var ties = completed.Count(g => g.HomeScore == g.AwayScore);
        var losses = completed.Length - wins - ties;
        bool Record(string[] row, int w, int l) => row[1] == w.ToString(CultureInfo.InvariantCulture) && row[2] == l.ToString(CultureInfo.InvariantCulture) && row[3] == ties.ToString(CultureInfo.InvariantCulture);
        if (!Record(current, wins, losses) || !Record(win, wins + 1, losses) || !Record(loss, wins, losses + 1)) return null;
        var wp = Probability(win[^1]); var lp = Probability(loss[^1]); var cp = Probability(current[^1]);
        if (wp is null || lp is null || cp is null || wp.Value.Lower > cp.Value.Upper || cp.Value.Lower > lp.Value.Upper) return null;
        var result = new NflConditionalPlayoffOdds
        {
            Reference = url, PublishedAt = page.PublishedAt!.Value,
            WinLower = 1 - wp.Value.Upper, WinUpper = 1 - wp.Value.Lower,
            LossLower = 1 - lp.Value.Upper, LossUpper = 1 - lp.Value.Lower
        };
        if (!NflPlayoffLeveragePolicy.Valid(result)) return null;
        if (Seeds(win) is not { } winSeeds || Seeds(loss) is not { } lossSeeds) return result;
        var seeded = result with { Seeds = new(winSeeds, lossSeeds) };
        return NflPlayoffLeveragePolicy.Positions(seeded) is null ? result : seeded;
    }

    private static ProbabilityInterval[]? Seeds(string[] row)
    {
        var cells = row[4..11].Select(Probability).ToArray();
        return cells.All(c => c is not null) ? cells.Select(c => new ProbabilityInterval(c!.Value.Lower, c.Value.Upper)).ToArray() : null;
    }

    public async Task<IReadOnlyDictionary<string, NflPlayoffLeverageEvidence?>> CollectAsync(IReadOnlyCollection<Game> games,
        IReadOnlyCollection<ProCompetitionEvidenceService.Schedule> schedules, CancellationToken ct)
    {
        var result = new Dictionary<string, NflPlayoffLeverageEvidence?>();
        var cache = new Dictionary<string, Task<Page>>();
        var leagues = new Dictionary<int, (League? Value, string? Problem)>();
        Task<Page> Fetch(string url)
        {
            if (!cache.TryGetValue(url, out var page)) cache[url] = page = FetchAsync(url, ct);
            return page;
        }
        var denied = false;
        foreach (var game in games.Where(g => g.Sport == "nfl" && g.SeasonType == 2 && g.SeasonYear.HasValue &&
            g.Status == "scheduled" && !g.IsHistoricalImport && !g.ActualStart.HasValue && g.ScheduledStart > clock.GetUtcNow().UtcDateTime))
        {
            if (denied) { result[game.Id] = null; continue; }
            var observation = new NflPlayoffLeverageEvidence
            {
                Version = 1, Source = NflPlayoffLeveragePolicy.Source, GameId = game.Id, Season = game.SeasonYear!.Value,
                Home = game.HomeTeamAbbreviation, Away = game.AwayTeamAbbreviation, ScheduledStart = game.ScheduledStart,
                ObservedAt = clock.GetUtcNow().UtcDateTime
            };
            try
            {
                result[game.Id] = observation;
                var page = await Fetch(NflPlayoffLeveragePolicy.ScheduleUrl);
                if (!leagues.TryGetValue(observation.Season, out var verified))
                {
                    var parsed = ParseLeague(page, observation.Season);
                    verified = (parsed, parsed is null ? "NFL source schedule, season or publication unavailable" : VerifyResults(parsed, schedules));
                    leagues[observation.Season] = verified;
                }
                if (verified.Problem == ResultPending) { result[game.Id] = null; continue; }
                var league = verified.Value;
                if (league is null || verified.Problem is not null) { observation.Reason = verified.Problem!; continue; }
                var home = Teams.SingleOrDefault(t => t.Abbreviation == Canonical(observation.Home));
                var away = Teams.SingleOrDefault(t => t.Abbreviation == Canonical(observation.Away));
                var match = league.Games.Where(g => g.Home == home?.Abbreviation && g.Away == away?.Abbreviation && g.Week == game.WeekNumber && !g.HomeScore.HasValue).ToArray();
                if (home is null || away is null || match.Length != 1 || new[] { home, away }.Any(t =>
                    league.Games.Where(g => !g.HomeScore.HasValue && (g.Home == t.Abbreviation || g.Away == t.Abbreviation)).OrderBy(g => g.Date).FirstOrDefault() != match[0]))
                { observation.Reason = "This is not both teams' verified next game on the NFL source schedule"; continue; }
                if (match[0].Start != game.ScheduledStart)
                {
                    observation.CorroboratedKickoff = CorroborateKickoff(game, match[0], schedules);
                    if (observation.CorroboratedKickoff is null)
                    { observation.Reason = "Kickoff mismatch lacks a corroborated verified next game in both ESPN schedules"; continue; }
                    observation.Version = 2;
                }
                var homeUrl = $"https://www.playoffstatus.com/nfl/{home.Slug}whatif.html";
                var awayUrl = $"https://www.playoffstatus.com/nfl/{away.Slug}whatif.html";
                observation.HomeOdds = ParseTeam(await Fetch(homeUrl), home, away, league, homeUrl);
                observation.AwayOdds = ParseTeam(await Fetch(awayUrl), away, home, league, awayUrl);
                observation.SchedulePublishedAt = page.PublishedAt!.Value;
                observation.PublicationLabel = league.Label;
                observation.CompletedGameIds = schedules.SelectMany(s => s.CompletedIds).Distinct().Order(StringComparer.Ordinal).ToArray();
                observation.ScheduleVerified = true;
                observation.ObservedAt = clock.GetUtcNow().UtcDateTime;
                observation.Reason = "PlayoffStatus NFL next-game model forecasts; complete schedule results corroborated with ESPN";
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && (ex is HttpRequestException or IOException or OperationCanceledException))
            {
                denied = ex is DeniedException;
                result[game.Id] = null;
                logger.LogWarning(ex, "NFL conditional forecast request failed for {GameId}; previous evidence clocks preserved", game.Id);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException or KeyNotFoundException)
            {
                observation.ScheduleVerified = false;
                observation.Reason = "Malformed or contradictory NFL source/schedule observation";
                logger.LogWarning(ex, "Rejected NFL conditional forecast observation for {GameId}", game.Id);
            }
        }
        return result;
    }
}
