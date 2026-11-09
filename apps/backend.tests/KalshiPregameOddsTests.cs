using System.Net;
using System.Text.Json.Nodes;
using backend.Scoring;
using backend.Services;
using backend.Services.Kalshi;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace backend.tests;

public sealed class KalshiPregameOddsTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 19, 56, 0, DateTimeKind.Utc);
    private static readonly DateTime Start = new(2026, 10, 11, 17, 0, 0, DateTimeKind.Utc);
    private const string Event = "KXNFLGAME-26OCT11INDPIT";
    private static JsonObject Capture() => JsonNode.Parse(ProHypeTests.Fixture("nfl-pregame-401872985")
        .GetProperty("kalshi").GetProperty("body").GetRawText())!.AsObject();

    private sealed class Handler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request.RequestUri!));
    }

    private static async Task<KalshiMoneylineOdds> Read(JsonObject body, string sport = "nfl", string home = "PIT", string away = "IND", DateTime? start = null)
    {
        using var handler = new Handler(url =>
        {
            Assert.Equal("/trade-api/v2/markets", url.AbsolutePath);
            Assert.Contains("status=open", url.Query);
            return new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) };
        });
        using var http = new HttpClient(handler);
        var service = new KalshiOddsService(http, NullLogger<KalshiOddsService>.Instance);
        return await service.GetPregameOddsAsync(sport, home, away, start ?? Start, Now, null);
    }

    [Fact]
    public async Task Synthetic_future_NFL_quotes_are_normalized_without_moneyline_roundtrip_or_live_bounds()
    {
        var odds = await Read(Capture());
        Assert.NotNull(odds); Assert.False(odds.IsLive); Assert.Equal("kalshi", odds.Source);
        Assert.Equal(.61 / 1.03, odds.HomeWinProbability, 12);
        Assert.Equal(.42 / 1.03, odds.AwayWinProbability, 12);
        Assert.Equal(81.55339805825243, 100 * (1 - Math.Abs(2 * odds.HomeWinProbability - 1)), 10);
    }

    [Theory]
    [InlineData("nfl", "KXNFLGAME", "WSH", "LAR", "WAS", "LAR")]
    [InlineData("nba", "KXNBAGAME", "WSH", "NO", "WAS", "NOP")]
    [InlineData("nhl", "KXNHLGAME", "UTAH", "TB", "UTA", "TB")]
    [InlineData("mlb", "KXMLBGAME", "NYY", "TB", "NYY", "TB")]
    public async Task Synthetic_league_and_alias_controls_use_the_existing_matcher(string sport, string series, string home, string away, string kalshiHome, string kalshiAway)
    {
        var body = Capture(); var markets = body["markets"]!.AsArray();
        var prefix = $"{series}-26OCT11{kalshiAway}{kalshiHome}";
        markets[0]!["ticker"] = prefix + "-" + kalshiAway; markets[0]!["event_ticker"] = prefix;
        markets[1]!["ticker"] = prefix + "-" + kalshiHome; markets[1]!["event_ticker"] = prefix;
        var odds = await Read(body, sport, home, away);
        Assert.NotNull(odds); Assert.Equal(home, odds.HomeTeamAbbr); Assert.Equal(away, odds.AwayTeamAbbr);
        Assert.Equal(.61 / 1.03, odds.HomeWinProbability, 12);
    }

    [Theory]
    [InlineData("missing-bid")]
    [InlineData("missing-ask")]
    [InlineData("crossed")]
    [InlineData("negative")]
    [InlineData("nan")]
    [InlineData("infinite")]
    [InlineData("terminal-bid")]
    [InlineData("terminal-ask")]
    [InlineData("missing-depth")]
    [InlineData("empty-depth")]
    [InlineData("inactive")]
    [InlineData("resolved")]
    [InlineData("scalar")]
    [InlineData("notional")]
    [InlineData("expired")]
    [InlineData("closes-before-game")]
    [InlineData("event-mismatch")]
    [InlineData("wrong-date")]
    [InlineData("wrong-sport")]
    [InlineData("missing-outcome")]
    [InlineData("duplicate-outcome")]
    [InlineData("wrong-start")]
    public async Task Synthetic_unusable_or_mismatched_quotes_never_become_pregame_closeness(string fault)
    {
        var body = Capture(); var markets = body["markets"]!.AsArray(); var market = markets[1]!;
        switch (fault)
        {
            case "missing-bid": market.AsObject().Remove("yes_bid_dollars"); break;
            case "missing-ask": market.AsObject().Remove("yes_ask_dollars"); break;
            case "crossed": market["yes_bid_dollars"] = "0.64"; break;
            case "negative": market["yes_bid_dollars"] = "-0.1"; break;
            case "nan": market["yes_bid_dollars"] = "NaN"; break;
            case "infinite": market["yes_ask_dollars"] = "Infinity"; break;
            case "terminal-bid": market["yes_bid_dollars"] = "0.00"; break;
            case "terminal-ask": market["yes_ask_dollars"] = "1.00"; break;
            case "missing-depth": market.AsObject().Remove("yes_bid_size_fp"); break;
            case "empty-depth": market["yes_ask_size_fp"] = "0.00"; break;
            case "inactive": market["status"] = "closed"; break;
            case "resolved": market["result"] = "yes"; break;
            case "scalar": market["market_type"] = "scalar"; break;
            case "notional": market["notional_value_dollars"] = "100.0"; break;
            case "expired": market["close_time"] = Now.ToString("O"); break;
            case "closes-before-game": market["close_time"] = Start.AddMinutes(-1).ToString("O"); break;
            case "event-mismatch": market["event_ticker"] = Event + "G1"; break;
            case "wrong-date": foreach (var m in markets) m!["ticker"] = m["ticker"]!.GetValue<string>().Replace("OCT11", "OCT12"); break;
            case "wrong-sport": foreach (var m in markets) m!["ticker"] = m["ticker"]!.GetValue<string>().Replace("KXNFLGAME", "KXNBAGAME"); break;
            case "missing-outcome": markets.RemoveAt(1); break;
            case "duplicate-outcome": markets.Add(market.DeepClone()); break;
            case "wrong-start": foreach (var m in markets) m!["ticker"] = m["ticker"]!.GetValue<string>().Replace("OCT11", "OCT111400"); break;
        }
        Assert.Null(await Read(body));
    }

    [Fact]
    public async Task Synthetic_doubleheaders_match_exact_Eastern_start_and_fail_closed_on_ambiguity()
    {
        var body = Capture(); var markets = body["markets"]!.AsArray();
        void Rename(JsonNode m, string prefix, string team) { m["ticker"] = prefix + "-" + team; m["event_ticker"] = prefix; }
        Rename(markets[0]!, "KXMLBGAME-26OCT111300INDPITG1", "IND");
        Rename(markets[1]!, "KXMLBGAME-26OCT111300INDPITG1", "PIT");
        var secondAway = markets[0]!.DeepClone(); var secondHome = markets[1]!.DeepClone();
        Rename(secondAway, "KXMLBGAME-26OCT111900INDPITG2", "IND"); Rename(secondHome, "KXMLBGAME-26OCT111900INDPITG2", "PIT");
        secondHome["yes_bid_dollars"] = "0.20"; secondHome["yes_ask_dollars"] = "0.22";
        secondAway["yes_bid_dollars"] = "0.78"; secondAway["yes_ask_dollars"] = "0.80";
        markets.Insert(0, secondHome); markets.Add(secondAway);
        Assert.Equal(.21, (await Read(body, "mlb", start: Start.AddHours(6))).HomeWinProbability, 12);
        Assert.Equal(.61 / 1.03, (await Read(body, "mlb")).HomeWinProbability, 12);
        Rename(secondHome, "KXMLBGAME-26OCT111300INDPITG2", "PIT"); Rename(secondAway, "KXMLBGAME-26OCT111300INDPITG2", "IND");
        Assert.Null(await Read(body, "mlb"));
    }

    [Fact]
    public async Task Incomplete_catalog_and_transport_errors_do_not_return_a_partial_match()
    {
        var body = Capture(); body["cursor"] = "repeated";
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) });
        using var http = new HttpClient(handler);
        var service = new KalshiOddsService(http, NullLogger<KalshiOddsService>.Instance);
        await Assert.ThrowsAsync<KalshiPregameCatalogIncompleteException>(() => service.GetPregameOddsAsync("nfl", "PIT", "IND", Start, Now, null));
        using var failedHandler = new Handler(_ => new(HttpStatusCode.ServiceUnavailable));
        using var failedHttp = new HttpClient(failedHandler);
        var failed = new KalshiOddsService(failedHttp, NullLogger<KalshiOddsService>.Instance);
        await Assert.ThrowsAsync<HttpRequestException>(() => failed.GetPregameOddsAsync("nfl", "PIT", "IND", Start, Now, null));
    }

    [Fact]
    public async Task Unsupported_or_already_started_games_do_not_query_Kalshi()
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Unexpected provider request"));
        using var http = new HttpClient(handler);
        var service = new KalshiOddsService(http, NullLogger<KalshiOddsService>.Instance);
        Assert.Null(await service.GetPregameOddsAsync("world_cup", "PIT", "IND", Start, Now, null));
        Assert.Null(await service.GetPregameOddsAsync("nfl", "PIT", "IND", Now, Now, null));
    }

    [Fact]
    public async Task Complete_catalog_cache_expires_and_successful_malformed_catalog_cannot_keep_old_quotes()
    {
        var requests = 0; var body = Capture().ToJsonString();
        using var handler = new Handler(_ =>
        {
            requests++;
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        using var http = new HttpClient(handler);
        var service = new KalshiOddsService(http, NullLogger<KalshiOddsService>.Instance);
        Assert.NotNull(await service.GetPregameOddsAsync("nfl", "PIT", "IND", Start, Now, null));
        Assert.NotNull(await service.GetPregameOddsAsync("nfl", "PIT", "IND", Start, Now.AddSeconds(29), null));
        Assert.Equal(1, requests);
        body = "not JSON";
        Assert.Null(await service.GetPregameOddsAsync("nfl", "PIT", "IND", Start, Now.AddSeconds(30), null));
        Assert.Equal(2, requests);
        body = "{}";
        Assert.Null(await service.GetPregameOddsAsync("nfl", "PIT", "IND", Start, Now.AddMinutes(1), null));
        Assert.Equal(3, requests);
    }

    [Theory]
    [InlineData("nfl")]
    [InlineData("nba")]
    [InlineData("nhl")]
    [InlineData("mlb")]
    public void Kalshi_pregame_rows_keep_the_same_identity_season_and_one_hour_expiry_checks(string sport)
    {
        var game = ProHypeTests.Game(sport); var odds = ProHypeTests.Odds(game); odds.Source = "kalshi";
        Assert.True(ProHypePolicy.EligibleOdds(game, odds, ProHypeTests.Now));
        Assert.False(ProHypePolicy.EligibleOdds(game, odds, ProHypeTests.Now.AddHours(1)));
        Assert.False(ProHypePolicy.EligibleOdds(game, odds, ProHypeTests.Now.AddTicks(-1)));
        odds.SourceSeason--; Assert.False(ProHypePolicy.EligibleOdds(game, odds, ProHypeTests.Now)); odds.SourceSeason++;
        odds.ApplicableStart = game.ScheduledStart.AddDays(1); Assert.False(ProHypePolicy.EligibleOdds(game, odds, ProHypeTests.Now));
        odds.ApplicableStart = game.ScheduledStart; odds.HomeTeamAbbreviation = "UNKNOWN";
        Assert.False(ProHypePolicy.EligibleOdds(game, odds, ProHypeTests.Now)); odds.HomeTeamAbbreviation = game.HomeTeamAbbreviation;
        odds.Source = GameWinnerProbabilityPolicy.OneSidedSource; Assert.False(ProHypePolicy.EligibleOdds(game, odds, ProHypeTests.Now));
        odds.Source = GameWinnerProbabilityPolicy.ResolvedSource; Assert.False(ProHypePolicy.EligibleOdds(game, odds, ProHypeTests.Now));
    }
}
