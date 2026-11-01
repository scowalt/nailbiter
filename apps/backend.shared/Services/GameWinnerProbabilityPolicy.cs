using System.Globalization;
using System.Text.Json;

namespace backend.Services;

public sealed record GameWinnerProbability(double HomeProbability, string Source);
public sealed record ThreeWayGameWinnerProbability(double HomeProbability, double DrawProbability, double AwayProbability, string Source);

public static class GameWinnerProbabilityPolicy
{
    public const string OneSidedSource = "Kalshi (near-certain orderbook bound)";
    public const string ResolvedSource = "Kalshi (resolved game-winner bound)";
    public const double WinnerRepresentative = .995;
    public const double LoserRepresentative = .005;

    public static bool IsBound(string? source) => source is OneSidedSource or ResolvedSource;

    public static bool FreshForDisplay(backend.Models.OddsSnapshot snapshot, DateTime now) =>
        !IsBound(snapshot.Source) || CollegeFootballPolicy.Fresh(snapshot.Timestamp, now, TimeSpan.FromMinutes(2));

    public static bool RequiresBound(JsonElement market) =>
        Price(market, "yes_bid") is null or 0m || Price(market, "yes_ask") is null or 1m ||
        Number(market, "yes_bid_size_fp") == 0m || Number(market, "yes_ask_size_fp") == 0m;

    private static string Text(JsonElement market, string field) =>
        market.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    private static decimal? Number(JsonElement market, string field)
    {
        if (!market.TryGetProperty(field, out var value)) return null;
        return decimal.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    private static decimal? Price(JsonElement market, string field) =>
        Number(market, field + "_dollars") ?? Number(market, field) / 100m;

    private static string Event(JsonElement market)
    {
        var ticker = Text(market, "ticker");
        var split = ticker.LastIndexOf('-');
        var inferred = split > 0 ? ticker[..split] : "";
        var declared = Text(market, "event_ticker");
        return declared == "" || declared == inferred ? inferred : "";
    }

    private static bool Active(JsonElement market, DateTime now) =>
        Text(market, "status") == "active" && Text(market, "result") == "" &&
        Text(market, "market_type") == "binary" && Number(market, "notional_value_dollars") == 1m &&
        DateTimeOffset.TryParse(Text(market, "close_time"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var close) && close.UtcDateTime > now;

    private static bool Uncrossed(JsonElement market)
    {
        var bid = Price(market, "yes_bid"); var ask = Price(market, "yes_ask");
        var validBid = bid is >= 0 and <= 1 || (bid is null && !(Number(market, "yes_bid_size_fp") > 0));
        var validAsk = ask is >= 0 and <= 1 || (ask is null && !(Number(market, "yes_ask_size_fp") > 0));
        return validBid && validAsk && (bid is null || ask is null || bid <= ask);
    }

    private static bool Resolved(JsonElement market, bool winner) =>
        Text(market, "status") is "determined" or "finalized" or "settled" &&
        Text(market, "market_type") == "binary" && Text(market, "result") == (winner ? "yes" : "no") &&
        Number(market, "notional_value_dollars") == 1m &&
        Price(market, "settlement_value") == (winner ? 1m : 0m);

    private static bool OneSidedWinner(JsonElement winner, JsonElement loser) =>
        Price(winner, "yes_bid") is >= .99m and <= 1m && Number(winner, "yes_bid_size_fp") > 0 &&
        Price(loser, "yes_ask") is >= 0m and <= .01m && Number(loser, "yes_ask_size_fp") > 0 &&
        (Price(winner, "yes_ask") is null or 1m || Number(winner, "yes_ask_size_fp") == 0m ||
         Price(loser, "yes_bid") is null or 0m || Number(loser, "yes_bid_size_fp") == 0m);

    public static ThreeWayGameWinnerProbability? ThreeWayFallback(JsonElement home, JsonElement draw, JsonElement away, DateTime now)
    {
        var markets = new[] { home, draw, away };
        var eventTicker = Event(home);
        if (eventTicker == "" || markets.Any(m => Event(m) != eventTicker) ||
            markets.Select(m => Text(m, "ticker")).Distinct().Count() != 3) return null;
        for (var winner = 0; winner < markets.Length; winner++)
        {
            var opponents = markets.Where((_, index) => index != winner).ToArray();
            string? source = null;
            if (Resolved(markets[winner], true) && opponents.All(m => Resolved(m, false))) source = ResolvedSource;
            else if (markets.All(m => Active(m, now) && Uncrossed(m)) &&
                     opponents.All(m => OneSidedWinner(markets[winner], m))) source = OneSidedSource;
            if (source is null) continue;
            var values = Enumerable.Range(0, 3).Select(i => i == winner ? WinnerRepresentative : LoserRepresentative / 2).ToArray();
            return new(values[0], values[1], values[2], source);
        }
        return null;
    }

    public static GameWinnerProbability? Fallback(JsonElement home, JsonElement away, DateTime now)
    {
        var eventTicker = Event(home);
        if (eventTicker == "" || eventTicker != Event(away) || Text(home, "ticker") == Text(away, "ticker")) return null;
        if (Resolved(home, true) && Resolved(away, false)) return new(WinnerRepresentative, ResolvedSource);
        if (Resolved(home, false) && Resolved(away, true)) return new(LoserRepresentative, ResolvedSource);
        if (!Active(home, now) || !Active(away, now) || !Uncrossed(home) || !Uncrossed(away)) return null;
        if (OneSidedWinner(home, away)) return new(WinnerRepresentative, OneSidedSource);
        if (OneSidedWinner(away, home)) return new(LoserRepresentative, OneSidedSource);
        return null;
    }
}
