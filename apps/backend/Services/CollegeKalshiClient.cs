using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace backend.Services;

public sealed record CollegeMarketPair(string HomeTicker, string AwayTicker);
public sealed record CollegeCandle(DateTime Timestamp, double HomeProbability);

public sealed class CollegeKalshiClient(HttpClient http, ILogger<CollegeKalshiClient> logger)
{
    private const string Base = "https://api.elections.kalshi.com/trade-api/v2";
    private const string Series = "KXNCAAFGAME";
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private bool accessRefused;

    private async Task<JsonElement> Get(string url, CancellationToken ct)
    {
        string body;
        try { body = await http.GetStringAsync(url, ct); }
        catch (KalshiAccessRefusedException) { accessRefused = true; throw; }
        using var json = JsonDocument.Parse(body);
        return json.RootElement.Clone();
    }

    public async Task<List<JsonElement>> ListingsAsync(DateTime since, bool includeArchive, CancellationToken ct)
    {
        var markets = new List<JsonElement>();
        var cutoff = includeArchive ? (await Get($"{Base}/historical/cutoff", ct)).At("market_settled_ts").Date() : null;
        var needsArchive = includeArchive && cutoff is DateTime boundary && since < boundary;
        foreach (var archive in needsArchive ? new[] { false, true } : new[] { false })
        {
            var seen = new HashSet<string>();
            var cursor = "";
            for (var page = 0; ; page++)
            {
                if (page >= 50) throw new InvalidDataException("College market pagination exceeded 50 pages");
                var route = archive ? "historical/markets" : "markets";
                var min = new DateTimeOffset(since).ToUnixTimeSeconds();
                var dateFilter = archive ? "" : $"&min_close_ts={min}";
                var data = await Get($"{Base}/{route}?series_ticker={Series}{dateFilter}&limit=200&cursor={Uri.EscapeDataString(cursor)}", ct);
                if (data.At("markets").ValueKind != JsonValueKind.Array) throw new InvalidDataException("Missing market listing");
                markets.AddRange(data.At("markets").Items());
                cursor = data.At("cursor").Text();
                if (cursor == "") break;
                if (!seen.Add(cursor)) throw new InvalidDataException("Repeated college market cursor");
            }
        }
        return markets.DistinctBy(m => m.At("ticker").Text()).ToList();
    }

    private static string Normalize(string name) => Regex.Replace(name.ToUpperInvariant(), "[^A-Z0-9]", "");
    private static bool TeamMatches(JsonElement market, string abbreviation, IEnumerable<string> names)
    {
        var suffix = market.At("ticker").Text().Split('-').Last();
        if (suffix == CollegeFootballPolicy.KalshiCode(abbreviation)) return true;
        var outcome = Normalize(market.At("yes_sub_title").Text());
        return outcome.Length > 0 && names.Any(n => !string.IsNullOrWhiteSpace(n) && Normalize(n) == outcome);
    }

    public static CollegeMarketPair? Match(IEnumerable<JsonElement> markets, DateTime start,
        string homeAbbr, string awayAbbr, IEnumerable<string> homeNames, IEnumerable<string> awayNames)
    {
        var date = TimeZoneInfo.ConvertTimeFromUtc(start, Eastern).ToString("yyMMMdd", CultureInfo.InvariantCulture).ToUpperInvariant();
        var matches = new List<CollegeMarketPair>();
        foreach (var group in markets.GroupBy(m => m.At("event_ticker").Text()))
        {
            if (!Regex.IsMatch(group.Key, $"^{Series}-{date}(?:[0-9]{{4}})?[A-Z0-9&]+$")) continue;
            var valid = group.Where(m => m.At("ticker").Text().StartsWith(group.Key + "-", StringComparison.Ordinal)).ToList();
            var home = valid.Where(m => TeamMatches(m, homeAbbr, homeNames)).ToList();
            var away = valid.Where(m => TeamMatches(m, awayAbbr, awayNames)).ToList();
            if (home.Count == 1 && away.Count == 1 && home[0].At("ticker").Text() != away[0].At("ticker").Text())
                matches.Add(new(home[0].At("ticker").Text(), away[0].At("ticker").Text()));
            else if (valid.Count == 1)
            {
                var h = CollegeFootballPolicy.KalshiCode(homeAbbr);
                var a = CollegeFootballPolicy.KalshiCode(awayAbbr);
                if (group.Key == $"{Series}-{date}{a}{h}" || group.Key == $"{Series}-{date}{h}{a}")
                    if (home.Count == 1 || away.Count == 1) matches.Add(new($"{group.Key}-{h}", $"{group.Key}-{a}"));
            }
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    public static double? Quote(JsonElement market, DateTime now)
    {
        if (market.At("status").Text() != "active" || market.At("result").Text() != ""
            || market.At("close_time").Date() is not DateTime close || close <= now) return null;
        var bid = market.At("yes_bid_dollars").Number();
        var ask = market.At("yes_ask_dollars").Number();
        var bidSize = market.At("yes_bid_size_fp").Number();
        var askSize = market.At("yes_ask_size_fp").Number();
        if (bid is not double b || ask is not double a || b <= 0 || a >= 1 || b > a || a - b > .20
            || !(bidSize > 0) || !(askSize > 0)) return null;
        return (b + a) / 2;
    }

    public async Task<double?> ProbabilityAsync(CollegeMarketPair pair, DateTime now, CancellationToken ct) =>
        (await ObserveProbabilityAsync(pair, now, ct))?.HomeProbability;

    public async Task<GameWinnerProbability?> ObservePregameProbabilityAsync(CollegeMarketPair pair, DateTime start, DateTime now, CancellationToken ct)
    {
        var date = TimeZoneInfo.ConvertTimeFromUtc(start, Eastern).ToString("yyMMMdd", CultureInfo.InvariantCulture).ToUpperInvariant();
        var prefix = $"{Series}-{date}";
        if (!pair.HomeTicker.StartsWith(prefix, StringComparison.Ordinal) || !pair.AwayTicker.StartsWith(prefix, StringComparison.Ordinal)) return null;
        return await Observe(pair, now, true, ct);
    }

    public Task<GameWinnerProbability?> ObserveProbabilityAsync(CollegeMarketPair pair, DateTime now, CancellationToken ct) =>
        Observe(pair, now, false, ct);

    private async Task<GameWinnerProbability?> Observe(CollegeMarketPair pair, DateTime now, bool pregame, CancellationToken ct)
    {
        if (accessRefused) return null;
        try
        {
            var home = (await Get($"{Base}/markets/{Uri.EscapeDataString(pair.HomeTicker)}", ct)).At("market");
            var away = (await Get($"{Base}/markets/{Uri.EscapeDataString(pair.AwayTicker)}", ct)).At("market");
            if (home.At("ticker").Text() != pair.HomeTicker || away.At("ticker").Text() != pair.AwayTicker) return null;
            if (pregame)
            {
                var eventTicker = pair.HomeTicker[..pair.HomeTicker.LastIndexOf('-')];
                if (home.At("event_ticker").Text() != eventTicker || away.At("event_ticker").Text() != eventTicker
                    || home.At("market_type").Text() != "binary" || away.At("market_type").Text() != "binary"
                    || home.At("notional_value_dollars").Number() != 1 || away.At("notional_value_dollars").Number() != 1) return null;
            }
            var fallback = GameWinnerProbabilityPolicy.Fallback(home, away, now);
            if (fallback is not null) return pregame && fallback.Source == GameWinnerProbabilityPolicy.ResolvedSource ? null : fallback;
            var h = Quote(home, now);
            var a = Quote(away, now);
            return h is not null && a is not null ? new(h.Value / (h.Value + a.Value), "Kalshi") : null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.Log(KalshiFailure.Is(ex) ? LogLevel.Error : LogLevel.Warning, ex, "College game quotes unavailable for {Ticker}", pair.HomeTicker);
            return null;
        }
    }

    public async Task<List<CollegeCandle>> HistoryAsync(CollegeMarketPair pair, DateTime start, DateTime end, CancellationToken ct)
    {
        try
        {
            var cutoff = (await Get($"{Base}/historical/cutoff", ct)).At("market_settled_ts").Date();
            var from = new DateTimeOffset(start).ToUnixTimeSeconds();
            var to = new DateTimeOffset(end).ToUnixTimeSeconds();
            async Task<Dictionary<DateTime, double>> Candles(string ticker)
            {
                var historical = cutoff is DateTime c && end < c;
                var route = historical ? $"historical/markets/{ticker}/candlesticks" : $"series/{Series}/markets/{ticker}/candlesticks";
                JsonElement data;
                try { data = await Get($"{Base}/{route}?start_ts={from}&end_ts={to}&period_interval=1", ct); }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    route = historical ? $"series/{Series}/markets/{ticker}/candlesticks" : $"historical/markets/{ticker}/candlesticks";
                    data = await Get($"{Base}/{route}?start_ts={from}&end_ts={to}&period_interval=1", ct);
                }
                return data.At("candlesticks").Items().Select(c => (Candle: c, Timestamp: c.At("end_period_ts").Number()))
                    .Where(x => x.Timestamp is not null)
                    .Select(x => (Time: DateTimeOffset.FromUnixTimeSeconds((long)x.Timestamp!.Value).UtcDateTime,
                        Price: x.Candle.At("price", "close_dollars").Number(), Volume: x.Candle.At("volume_fp").Number() ?? x.Candle.At("volume").Number()))
                    .Where(x => x.Time.AddMinutes(-1) >= start && x.Time < end && x.Price is > 0 and < 1 && x.Volume > 0)
                    .GroupBy(x => x.Time).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Price!.Value);
            }
            var h = await Candles(pair.HomeTicker);
            var a = await Candles(pair.AwayTicker);
            return h.Where(p => a.ContainsKey(p.Key)).OrderBy(p => p.Key)
                .Select(p => new CollegeCandle(p.Key, p.Value / (p.Value + a[p.Key]))).ToList();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "College market history unavailable for {Ticker}", pair.HomeTicker);
            return [];
        }
    }
}
