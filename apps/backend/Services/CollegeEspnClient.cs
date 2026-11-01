using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using backend.Models;
using backend.Services.Espn;
using Microsoft.Extensions.Caching.Memory;

namespace backend.Services;

internal static class CollegeJson
{
    public static JsonElement At(this JsonElement value, params string[] path)
    {
        foreach (var key in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out value)) return default;
        }
        return value;
    }
    public static string Text(this JsonElement value) => value.ValueKind is JsonValueKind.String ? value.GetString()! : value.ToString();
    public static int? Int(this JsonElement value) => int.TryParse(value.Text(), out var n) ? n : null;
    public static double? Number(this JsonElement value) => double.TryParse(value.Text(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
    public static DateTime? Date(this JsonElement value) => DateTimeOffset.TryParse(value.Text(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : null;
    public static IEnumerable<JsonElement> Items(this JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
}

public sealed record CollegeSchedule(IReadOnlyList<JsonElement> Events, DateTime? SeasonStart);

public sealed class CollegeEspnClient(HttpClient http, TimeProvider time, IMemoryCache cache, ILogger<CollegeEspnClient> logger)
{
    private const string Path = "/apis/site/v2/sports/football/college-football";
    private const string Core = "https://sports.core.api.espn.com/v2/sports/football/leagues/college-football";

    private async Task<JsonElement> Get(string url, CancellationToken ct)
    {
        if (cache.TryGetValue<JsonElement>(url, out var cached)) return cached;
        using var json = JsonDocument.Parse(await http.GetStringAsync(url, ct));
        var result = json.RootElement.Clone();
        if (url.Contains("/events?dates=", StringComparison.Ordinal)) cache.Set(url, result, TimeSpan.FromMinutes(15));
        else if (url.Contains("/summary?", StringComparison.Ordinal)
                 && !result.At("header", "competitions").Items().Any(c => c.At("status", "type", "state").Text() == "in"))
            cache.Set(url, result, TimeSpan.FromMinutes(5));
        return result;
    }

    public async Task<List<CollegeTeam>> RosterAsync(int season, CancellationToken ct)
    {
        var result = new List<CollegeTeam>();
        var pages = 1;
        var count = 0;
        for (var page = 1; page <= pages; page++)
        {
            var data = await Get(EspnSiteApi.url($"/apis/fitt/v3/sports/football/college-football/powerindex?region=us&lang=en&season={season}&page={page}&limit=200"), ct);
            if (data.At("requestedSeason", "year").Int() != season || data.At("currentValues", "season").Int() != season
                || data.At("currentValues", "conference").Text() != "80")
                throw new InvalidDataException("FBS roster lacks matching season/group evidence");
            pages = data.At("pagination", "pages").Int() ?? 0;
            count = data.At("pagination", "count").Int() ?? 0;
            if (pages is < 1 or > 10 || data.At("pagination", "page").Int() != page)
                throw new InvalidDataException("Invalid FPI pagination");
            var columns = data.At("categories").Items().Single(c => c.At("name").Text() == "fpi")
                .At("names").Items().Select(c => c.Text()).ToList();
            var index = columns.IndexOf("fpi");
            if (index < 0) throw new InvalidDataException("FPI column missing");
            var updated = data.At("lastUpdated").Date();
            foreach (var row in data.At("teams").Items())
            {
                var team = row.At("team");
                var values = row.At("categories").Items().FirstOrDefault(c => c.At("name").Text() == "fpi").At("values").Items().ToArray();
                result.Add(new CollegeTeam
                {
                    Season = season,
                    EspnId = team.At("id").Text(),
                    Abbreviation = team.At("abbreviation").Text(),
                    Name = team.At("displayName").Text(),
                    Fpi = updated <= time.GetUtcNow().UtcDateTime && data.At("currentSeason", "year").Int() == season && index < values.Length ? values[index].Number() : null,
                    FpiUpdatedAt = updated ?? DateTime.UnixEpoch,
                    FetchedAt = time.GetUtcNow().UtcDateTime
                });
            }
        }
        if (result.Count != count || result.Count == 0 || result.Select(t => t.EspnId).Distinct().Count() != count
            || result.Any(t => string.IsNullOrEmpty(t.EspnId) || string.IsNullOrEmpty(t.Abbreviation)))
            throw new InvalidDataException("Incomplete or ambiguous FBS roster");
        return result;
    }

    public async Task<(DateTime Date, Dictionary<string, int> Ranks)?> ApPollAsync(int season, CancellationToken ct)
    {
        var data = await Get(EspnSiteApi.url($"{Path}/rankings?seasons={season}"), ct);
        var poll = data.At("rankings").Items().SingleOrDefault(p => p.At("id").Text() == "1" && p.At("type").Text() == "ap");
        var date = poll.At("lastUpdated").Date() ?? poll.At("date").Date();
        if (poll.At("season", "year").Int() != season || date is null || date > time.GetUtcNow().UtcDateTime) return null;
        var ranks = poll.At("ranks").Items().Where(r => r.At("current").Int() is >= 1 and <= 25)
            .ToDictionary(r => r.At("team", "id").Text(), r => r.At("current").Int()!.Value);
        return (date.Value, ranks);
    }

    public async Task<CollegeSchedule> ScheduleAsync(DateTime date, int season, bool traverse, CancellationToken ct)
    {
        var cacheKey = $"college-schedule:{season}:{date:yyyyMMdd}";
        if (cache.TryGetValue<CollegeSchedule>(cacheKey, out var cached) && cached is not null) return cached;
        var data = await Get(EspnSiteApi.url($"{Path}/scoreboard?dates={date:yyyyMMdd}&groups=80&limit=100"), ct);
        var events = data.At("events").Items().ToDictionary(e => e.At("id").Text());
        var league = data.At("leagues").Items().FirstOrDefault();
        var start = league.At("season", "year").Int() == season
            ? league.At("calendar").Items().FirstOrDefault(c => c.At("value").Text() == "2").At("startDate").Date() : null;
        if (traverse)
        {
            var pages = 1;
            var ids = new HashSet<string>();
            var count = 0;
            for (var page = 1; page <= pages; page++)
            {
                var index = await Get($"{Core}/events?dates={date:yyyyMMdd}&limit=100&page={page}", ct);
                pages = index.At("pageCount").Int() ?? -1;
                count = index.At("count").Int() ?? -1;
                var pageIndex = index.At("pageIndex").Int();
                var items = index.At("items");
                if (page == 1 && pages == 0 && pageIndex == 0 && count == 0
                    && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() == 0) break;
                if (pages is < 1 or > 20 || page > pages || pageIndex != page || count < 0 || items.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException($"Invalid college event pagination for {date:yyyy-MM-dd} (requested page {page}, index {pageIndex}, pages {pages}, count {count})");
                foreach (var item in items.EnumerateArray())
                {
                    var match = Regex.Match(item.At("$ref").Text(), @"/events/(\d+)\?");
                    if (!match.Success || !ids.Add(match.Groups[1].Value)) throw new InvalidDataException("Invalid/duplicate college event reference");
                }
            }
            if (ids.Count != count) throw new InvalidDataException("Partial college event index");
            foreach (var id in ids.Where(id => !events.ContainsKey(id)))
            {
                var summary = await Get(EspnSiteApi.url($"{Path}/summary?event={id}"), ct);
                var header = summary.At("header");
                events[id] = header;
            }
        }
        var schedule = new CollegeSchedule(events.Values.ToList(), start);
        var today = TimeZoneInfo.ConvertTimeFromUtc(time.GetUtcNow().UtcDateTime, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).Date;
        if (date.Date > today || date.Date < today.AddDays(-1)) cache.Set(cacheKey, schedule, TimeSpan.FromMinutes(15));
        return schedule;
    }

    public async Task<JsonElement> EventAsync(string id, CancellationToken ct) =>
        (await Get(EspnSiteApi.url($"{Path}/summary?event={Uri.EscapeDataString(id)}"), ct)).At("header");

    public async Task<(DateTime Start, DateTime End)?> PlayWindowAsync(string eventId, CancellationToken ct)
    {
        try
        {
            var timestamps = new List<DateTime>();
            var pages = 1;
            for (var page = 1; page <= pages; page++)
            {
                var data = await Get($"{Core}/events/{eventId}/competitions/{eventId}/plays?limit=1000&page={page}", ct);
                pages = data.At("pageCount").Int() ?? 0;
                if (pages is < 1 or > 20 || data.At("pageIndex").Int() != page) return null;
                foreach (var play in data.At("items").Items())
                    if (play.At("period", "number").Int() > 0 && play.At("wallclock").Date() is DateTime stamp)
                        timestamps.Add(stamp);
            }
            return timestamps.Count >= 2 ? (timestamps.Min(), timestamps.Max()) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        { logger.LogWarning(ex, "Historical play bounds unavailable for {Event}", eventId); return null; }
    }

    public async Task<PregameOdds?> ClosingOddsAsync(string eventId, CancellationToken ct)
    {
        try
        {
            var data = await Get($"{Core}/events/{eventId}/competitions/{eventId}/odds", ct);
            foreach (var item in data.At("items").Items())
            {
                var h = item.At("homeTeamOdds", "close", "moneyLine", "american").Int();
                var a = item.At("awayTeamOdds", "close", "moneyLine", "american").Int();
                if (h is null || a is null || Math.Abs(h.Value) < 100 || Math.Abs(a.Value) < 100) continue;
                var (hp, ap) = OddsConverter.ToNormalizedProbabilities(h.Value, a.Value);
                return new PregameOdds
                {
                    GameId = eventId,
                    HomeMoneyline = h.Value,
                    AwayMoneyline = a.Value,
                    HomeWinProbability = hp,
                    AwayWinProbability = ap,
                    Spread = item.At("homeTeamOdds", "close", "pointSpread", "american").Number(),
                    OverUnder = item.At("close", "total", "american").Number(),
                    FetchedAt = time.GetUtcNow().UtcDateTime,
                    Source = $"ESPN {item.At("provider", "name").Text()} historical closing line (retrieved, not quoted, at)"
                };
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogWarning(ex, "Closing lines unavailable for college event {Event}", eventId);
        }
        return null;
    }

    public static PregameOdds? Pregame(JsonElement competition, string id, DateTime now)
    {
        foreach (var odds in competition.At("odds").Items())
        {
            var home = OddsConverter.TryParseMoneyline(odds.At("moneyline", "home", "close", "odds").Text());
            var away = OddsConverter.TryParseMoneyline(odds.At("moneyline", "away", "close", "odds").Text());
            if (home is null || away is null || Math.Abs(home.Value) < 100 || Math.Abs(away.Value) < 100) continue;
            var (hp, ap) = OddsConverter.ToNormalizedProbabilities(home.Value, away.Value);
            return new PregameOdds
            {
                GameId = id,
                HomeMoneyline = home.Value,
                AwayMoneyline = away.Value,
                HomeWinProbability = hp,
                AwayWinProbability = ap,
                Spread = odds.At("spread").Number(),
                OverUnder = odds.At("overUnder").Number(),
                FetchedAt = now,
                Source = $"ESPN {odds.At("provider", "name").Text()}"
            };
        }
        return null;
    }
}
