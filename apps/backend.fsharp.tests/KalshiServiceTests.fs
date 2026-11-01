module KalshiServiceTests

open System
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open backend.Services.Espn
open backend.Services.Kalshi

[<Fact>]
let ``SupportsSport - nfl is supported`` () =
    Assert.True(KalshiOddsService.SupportsSport("nfl"))

[<Fact>]
let ``SupportsSport - nba is supported`` () =
    Assert.True(KalshiOddsService.SupportsSport("nba"))

[<Fact>]
let ``SupportsSport - nhl is supported`` () =
    Assert.True(KalshiOddsService.SupportsSport("nhl"))

[<Fact>]
let ``SupportsSport - mlb is supported`` () =
    Assert.True(KalshiOddsService.SupportsSport("mlb"))

[<Fact>]
let ``SupportsSport - empty string is not supported`` () =
    Assert.False(KalshiOddsService.SupportsSport(""))

[<Fact>]
let ``SupportsSport - arbitrary string is not supported`` () =
    Assert.False(KalshiOddsService.SupportsSport("cricket"))

[<Fact>]
let ``SupportsSport - case insensitive for NFL`` () =
    Assert.True(KalshiOddsService.SupportsSport("NFL"))
    Assert.True(KalshiOddsService.SupportsSport("Nfl"))

[<Fact>]
let ``SupportsSport - case insensitive for NBA`` () =
    Assert.True(KalshiOddsService.SupportsSport("NBA"))
    Assert.True(KalshiOddsService.SupportsSport("Nba"))

[<Fact>]
let ``SupportsSport - case insensitive for NHL`` () =
    Assert.True(KalshiOddsService.SupportsSport("NHL"))
    Assert.True(KalshiOddsService.SupportsSport("Nhl"))

[<Theory>]
[<InlineData("STATUS_OVERTIME")>]
[<InlineData("STATUS_EXTRA_TIME")>]
[<InlineData("STATUS_SHOOTOUT")>]
[<InlineData("STATUS_PENALTY_SHOOTOUT")>]
let ``World Cup advance phase recognizes extra time and shootouts`` statusName =
    Assert.True(WorldCupStatus.isAdvancePhase statusName)

[<Fact>]
let ``World Cup advance phase excludes regulation`` () =
    Assert.False(WorldCupStatus.isAdvancePhase "STATUS_IN_PROGRESS")

let private kalshiServiceWithResponse (json: string) =
    let handler =
        { new HttpMessageHandler() with
            override _.SendAsync(request: HttpRequestMessage, _ct: CancellationToken) =
                let response = new HttpResponseMessage(HttpStatusCode.OK)
                response.Content <- new StringContent(json)
                Task.FromResult(response) }

    new KalshiOddsService(new HttpClient(handler), NullLogger<KalshiOddsService>.Instance)

let private kalshiServiceWithRoutes (routes: Map<string, string>) =
    let requests = ResizeArray<Uri>()
    let handler =
        { new HttpMessageHandler() with
            override _.SendAsync(request: HttpRequestMessage, _ct: CancellationToken) =
                requests.Add(request.RequestUri)
                let response =
                    match routes.TryFind(request.RequestUri.AbsolutePath) with
                    | Some json ->
                        let response = new HttpResponseMessage(HttpStatusCode.OK)
                        response.Content <- new StringContent(json)
                        response
                    | None -> new HttpResponseMessage(HttpStatusCode.NotFound)
                Task.FromResult(response) }

    new KalshiOddsService(new HttpClient(handler), NullLogger<KalshiOddsService>.Instance), requests

let private washingtonGameDate = DateTime(2026, 9, 13, 20, 25, 0, DateTimeKind.Utc)

let private washingtonMarkets (series: string) (washington: string) =
    let prefix = $"{series}-26SEP13{washington}PHI"
    let home = $"""{{ "ticker": "{prefix}-PHI", "yes_bid_dollars": "0.6900", "yes_ask_dollars": "0.7100", "result": "" }}"""
    let away = $"""{{ "ticker": "{prefix}-{washington}", "yes_bid_dollars": "0.2900", "yes_ask_dollars": "0.3100", "result": "" }}"""
    prefix, home, away

let private boundMarket prefix team winner resolved =
    let status = if resolved then "determined" else "active"
    let result = if resolved then (if winner then "yes" else "no") else ""
    let settlement = if winner then "1.0000" else "0.0000"
    let bid, ask, bidSize, askSize = if winner then "0.99", "1.00", "10", "0" else "0.00", "0.01", "0", "10"
    $"""{{ "ticker": "{prefix}-{team}", "event_ticker": "{prefix}", "status": "{status}", "result": "{result}", "market_type": "binary", "notional_value_dollars": "1.0000", "settlement_value_dollars": "{settlement}", "close_time": "2099-01-01T00:00:00Z", "yes_bid_dollars": "{bid}", "yes_ask_dollars": "{ask}", "yes_bid_size_fp": "{bidSize}", "yes_ask_size_fp": "{askSize}" }}"""

[<Theory>]
[<InlineData("nfl", "KXNFLGAME", false)>]
[<InlineData("nfl", "KXNFLGAME", true)>]
[<InlineData("nba", "KXNBAGAME", false)>]
[<InlineData("nba", "KXNBAGAME", true)>]
[<InlineData("nhl", "KXNHLGAME", false)>]
[<InlineData("nhl", "KXNHLGAME", true)>]
[<InlineData("mlb", "KXMLBGAME", false)>]
[<InlineData("mlb", "KXMLBGAME", true)>]
let ``two outcome sports preserve near certain bounds and provenance`` sport series resolved = task {
    let prefix = $"{series}-26SEP13WASPHI"
    let home = boundMarket prefix "PHI" true resolved
    let away = boundMarket prefix "WAS" false resolved
    let service = kalshiServiceWithResponse $"""{{ "markets": [{home}, {away}], "cursor": "" }}"""
    let! odds = service.GetLiveOddsAsync(sport, "PHI", "WAS", washingtonGameDate)
    Assert.NotNull(odds)
    Assert.Equal(0.995, odds.HomeWinProbability)
    Assert.Equal(0.005, odds.AwayWinProbability, 10)
    Assert.Equal((if resolved then backend.Services.GameWinnerProbabilityPolicy.ResolvedSource else backend.Services.GameWinnerProbabilityPolicy.OneSidedSource), odds.Source)
}

[<Theory>]
[<InlineData(false, "USA")>]
[<InlineData(true, "USA")>]
[<InlineData(false, "TIE")>]
[<InlineData(true, "TIE")>]
let ``World Cup three way bounds require evidence for every outcome`` resolved winner = task {
    let prefix = "KXWCGAME-26JUN19AUSUSA"
    let markets = ["USA"; "TIE"; "AUS"] |> List.map (fun team -> boundMarket prefix team (team = winner) resolved) |> String.concat ","
    let service = kalshiServiceWithResponse $"""{{ "markets": [{markets}], "cursor": "" }}"""
    let! odds = service.GetWorldCupThreeWayLiveOddsAsync("USA", "AUS", DateTime(2026, 6, 19, 19, 0, 0, DateTimeKind.Utc))
    Assert.NotNull(odds)
    Assert.Equal((if winner = "USA" then 0.995 else 0.0025), odds.HomeWinProbability, 10)
    Assert.Equal((if winner = "TIE" then 0.995 else 0.0025), odds.DrawWinProbability, 10)
    Assert.Equal(0.0025, odds.AwayWinProbability, 10)
    Assert.Equal((if resolved then backend.Services.GameWinnerProbabilityPolicy.ResolvedSource else backend.Services.GameWinnerProbabilityPolicy.OneSidedSource), odds.Source)
}

[<Fact>]
let ``World Cup bounds cannot omit draw evidence`` () = task {
    let prefix = "KXWCGAME-26JUN19AUSUSA"
    let home = boundMarket prefix "USA" true false
    let away = boundMarket prefix "AUS" false false
    let service = kalshiServiceWithResponse $"""{{ "markets": [{home}, {away}], "cursor": "" }}"""
    let! odds = service.GetWorldCupThreeWayLiveOddsAsync("USA", "AUS", DateTime(2026, 6, 19, 19, 0, 0, DateTimeKind.Utc))
    Assert.Null(odds)
}

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``World Cup advancement keeps complementary bounds`` resolved = task {
    let prefix = "KXWCADVANCE-26JUL11ENGNOR"
    let home = boundMarket prefix "NOR" true resolved
    let away = boundMarket prefix "ENG" false resolved
    let service = kalshiServiceWithResponse $"""{{ "markets": [{home}, {away}], "cursor": "" }}"""
    let! odds = service.GetWorldCupAdvanceLiveOddsAsync("NOR", "ENG", DateTime(2026, 7, 11, 21, 0, 0, DateTimeKind.Utc))
    Assert.NotNull(odds)
    Assert.Equal(0.995, odds.HomeWinProbability)
    Assert.Equal((if resolved then backend.Services.GameWinnerProbabilityPolicy.ResolvedSource else backend.Services.GameWinnerProbabilityPolicy.OneSidedSource), odds.Source)
}

[<Fact>]
let ``direct lookup accepts confirmed winner without stale midpoint fallback`` () = task {
    let prefix = "KXNFLGAME-26SEP13WASPHI"
    let routes = Map.ofList [
        "/trade-api/v2/markets", """{"markets": [], "cursor": ""}"""
        $"/trade-api/v2/markets/{prefix}-PHI", $"""{{ "market": {boundMarket prefix "PHI" true true} }}"""
        $"/trade-api/v2/markets/{prefix}-WAS", $"""{{ "market": {boundMarket prefix "WAS" false true} }}""" ]
    let service, _ = kalshiServiceWithRoutes routes
    let! odds = service.GetLiveOddsAsync("nfl", "PHI", "WAS", washingtonGameDate)
    Assert.NotNull(odds)
    Assert.Equal(backend.Services.GameWinnerProbabilityPolicy.ResolvedSource, odds.Source)
}

[<Fact>]
let ``one sided market without depth cannot leak through legacy midpoint path`` () = task {
    let prefix = "KXNFLGAME-26SEP13WASPHI"
    let home = (boundMarket prefix "PHI" true false).Replace("\"yes_bid_size_fp\": \"10\"", "\"yes_bid_size_fp\": \"0\"")
    let away = boundMarket prefix "WAS" false false
    let service = kalshiServiceWithResponse $"""{{ "markets": [{home}, {away}], "cursor": "" }}"""
    let! odds = service.GetLiveOddsAsync("nfl", "PHI", "WAS", washingtonGameDate)
    Assert.Null(odds)
}

[<Theory>]
[<InlineData("nfl", "KXNFLGAME", "PHI", "WSH", "WAS")>]
[<InlineData("nfl", "KXNFLGAME", "WSH", "PHI", "WAS")>]
[<InlineData("nfl", "KXNFLGAME", "PHI", "WAS", "WAS")>]
[<InlineData("NFL", "KXNFLGAME", "PHI", "wsh", "WAS")>]
[<InlineData("nba", "KXNBAGAME", "PHI", "WSH", "WAS")>]
[<InlineData("mlb", "KXMLBGAME", "PHI", "WSH", "WSH")>]
let ``GetLiveOddsAsync matches Washington aliases by sport`` sport series homeTeam awayTeam washington = task {
    let _, home, away = washingtonMarkets series washington
    let service, requests =
        kalshiServiceWithRoutes (Map.ofList [
            "/trade-api/v2/markets", $"""{{ "cursor": "", "markets": [{home}, {away}] }}"""
        ])

    let! odds = service.GetLiveOddsAsync(sport, homeTeam, awayTeam, washingtonGameDate)

    Assert.NotNull(box odds)
    Assert.Equal(homeTeam, odds.HomeTeamAbbr)
    Assert.Equal(awayTeam, odds.AwayTeamAbbr)
    Assert.Equal($"{awayTeam}@{homeTeam}", odds.GameId)
    let expectedHomeProbability = if homeTeam = "PHI" then 0.7 else 0.3
    Assert.Equal(expectedHomeProbability, odds.HomeWinProbability, 6)
    Assert.Equal(1.0 - expectedHomeProbability, odds.AwayWinProbability, 6)
    Assert.True(odds.IsLive)
    let request = Assert.Single(requests)
    Assert.Equal($"?series_ticker={series}&limit=200", request.Query)
}

[<Theory>]
[<InlineData("PHI", "WSH")>]
[<InlineData("WSH", "PHI")>]
let ``GetLiveOddsAsync uses Washington NFL alias in direct ticker fallback`` homeTeam awayTeam = task {
    let prefix, home, away = washingtonMarkets "KXNFLGAME" "WAS"
    let service, _ =
        kalshiServiceWithRoutes (Map.ofList [
            "/trade-api/v2/markets", """{ "cursor": "", "markets": [] }"""
            $"/trade-api/v2/markets/{prefix}-PHI", $"""{{ "market": {home} }}"""
            $"/trade-api/v2/markets/{prefix}-WAS", $"""{{ "market": {away} }}"""
        ])

    let! odds = service.GetLiveOddsAsync("nfl", homeTeam, awayTeam, washingtonGameDate)

    Assert.NotNull(box odds)
    Assert.Equal(homeTeam, odds.HomeTeamAbbr)
    Assert.Equal(awayTeam, odds.AwayTeamAbbr)
    Assert.Equal((if homeTeam = "PHI" then 0.7 else 0.3), odds.HomeWinProbability, 6)
    Assert.Equal((if awayTeam = "PHI" then 0.7 else 0.3), odds.AwayWinProbability, 6)
}

[<Fact>]
let ``GetHistoricalOddsAsync uses Washington NFL alias for candlesticks`` () = task {
    let service, _ =
        kalshiServiceWithRoutes (Map.ofList [
            "/trade-api/v2/series/KXNFLGAME/markets/KXNFLGAME-26SEP13WASPHI-PHI/candlesticks",
            """{ "candlesticks": [{ "end_period_ts": 1789331400, "price": { "close": 70 }, "volume": 100 }] }"""
        ])

    let! odds = service.GetHistoricalOddsAsync("nfl", "PHI", "WSH", washingtonGameDate)

    let snapshot = Assert.Single(odds)
    Assert.Equal(0.7, snapshot.HomeWinProbability, 6)
    Assert.Equal(0.3, snapshot.AwayWinProbability, 6)
}

[<Theory>]
[<InlineData("\"close_dollars\": \"0.4321\"", 0.4321)>]
[<InlineData("\"close_dollars\": \"0.4321\", \"close\": 70", 0.4321)>]
[<InlineData("\"close\": 70", 0.7)>]
[<InlineData("\"close_dollars\": null, \"close\": 70", 0.7)>]
[<InlineData("\"close_dollars\": \"\", \"close\": 70", 0.7)>]
[<InlineData("\"close_dollars\": \"0.0000\"", 0.0)>]
[<InlineData("\"close_dollars\": \"1.0000\"", 1.0)>]
let ``GetHistoricalOddsAsync reads dollar and legacy closing prices`` (priceFields: string) expectedProbability = task {
    let service, _ =
        kalshiServiceWithRoutes (Map.ofList [
            "/trade-api/v2/series/KXNFLGAME/markets/KXNFLGAME-26SEP13WASPHI-PHI/candlesticks",
            $"""{{ "candlesticks": [{{ "end_period_ts": 1789331100, "price": {{ {priceFields} }}, "volume_fp": "27616.03" }}] }}"""
        ])

    let! odds = service.GetHistoricalOddsAsync("nfl", "PHI", "WSH", washingtonGameDate)

    let snapshot = Assert.Single(odds)
    Assert.Equal(expectedProbability, snapshot.HomeWinProbability, 6)
    Assert.Equal(1.0 - expectedProbability, snapshot.AwayWinProbability, 6)
    Assert.Equal(washingtonGameDate, snapshot.Timestamp)
}

[<Theory>]
[<InlineData("null")>]
[<InlineData("{}")>]
[<InlineData("{\"close_dollars\": null}")>]
[<InlineData("{\"close_dollars\": \"invalid\"}")>]
[<InlineData("{\"close_dollars\": \"NaN\"}")>]
[<InlineData("{\"close_dollars\": \"Infinity\"}")>]
[<InlineData("{\"close_dollars\": \"-0.0100\"}")>]
[<InlineData("{\"close_dollars\": \"1.0100\"}")>]
[<InlineData("{\"close\": 101}")>]
let ``GetHistoricalOddsAsync skips unusable prices without losing valid history`` (invalidPrice: string) = task {
    let service, _ =
        kalshiServiceWithRoutes (Map.ofList [
            "/trade-api/v2/series/KXNFLGAME/markets/KXNFLGAME-26SEP13WASPHI-PHI/candlesticks",
            $"""{{ "candlesticks": [
              {{ "end_period_ts": 1789331220, "price": {{ "close_dollars": "0.6000" }} }},
              {{ "end_period_ts": 1789331160, "price": {invalidPrice} }},
              {{ "end_period_ts": 1789331100, "price": {{ "close": 70 }} }}
            ] }}"""
        ])

    let! odds = service.GetHistoricalOddsAsync("nfl", "PHI", "WSH", washingtonGameDate)

    Assert.Equal(2, odds.Count)
    Assert.Equal(washingtonGameDate, odds[0].Timestamp)
    Assert.Equal(0.7, odds[0].HomeWinProbability, 6)
    Assert.Equal(washingtonGameDate.AddMinutes(2), odds[1].Timestamp)
    Assert.Equal(0.6, odds[1].HomeWinProbability, 6)
}

[<Fact>]
let ``FindMarketTickerAsync uses Washington NFL alias`` () = task {
    let prefix, home, _ = washingtonMarkets "KXNFLGAME" "WAS"
    let ticker = $"{prefix}-PHI"
    let service, requests =
        kalshiServiceWithRoutes (Map.ofList [
            $"/trade-api/v2/markets/{ticker}", $"""{{ "market": {home} }}"""
        ])

    let! result = service.FindMarketTickerAsync("nfl", "PHI", "WSH", washingtonGameDate)

    Assert.Equal(ticker, result)
    Assert.Single(requests) |> ignore
}

let nflAliasCases: obj array seq =
    seq {
        for sport, home, away, marketHome, marketAway, date in [
            "nfl", "LAR", "NYG", "LAR", "NYG", "2026-09-22T00:15:00Z"
            "nfl", "LAC", "LAR", "LAC", "LAR", "2026-08-28T02:00:00Z"
            "nfl", "DET", "NO", "DET", "NO", "2026-09-13T17:00:00Z"
            "nfl", "NO", "ATL", "NO", "ATL", "2026-09-13T17:00:00Z"
            "NFL", "BAL", "no", "BAL", "NO", "2026-09-20T17:00:00Z"
            "nba", "NO", "BOS", "NOP", "BOS", "2026-09-13T17:00:00Z"
            "nba", "BOS", "NO", "BOS", "NOP", "2026-09-13T17:00:00Z"
        ] do
            for operation in [ "listed"; "direct"; "discovery"; "historical" ] do
                yield [| box sport; box home; box away; box marketHome; box marketAway; box date; box operation |]
    }

[<Theory>]
[<MemberData(nameof nflAliasCases)>]
let ``NFL aliases preserve Rams and Saints codes without changing NBA``
    (sport: string) (home: string) (away: string) (marketHome: string) (marketAway: string) (date: string) operation = task {
    let gameDate = DateTimeOffset.Parse(date).UtcDateTime
    let eastern = TimeZoneInfo.ConvertTimeFromUtc(gameDate, TimeZoneInfo.FindSystemTimeZoneById("America/New_York"))
    let dateCode = eastern.ToString("yyMMMdd", Globalization.CultureInfo.InvariantCulture).ToUpperInvariant()
    let series = if sport.Equals("nfl", StringComparison.OrdinalIgnoreCase) then "KXNFLGAME" else "KXNBAGAME"
    let eventTicker = $"{series}-{dateCode}{marketAway}{marketHome}"
    let homeTicker = $"{eventTicker}-{marketHome}"
    let awayTicker = $"{eventTicker}-{marketAway}"
    let homeMarket = $"""{{ "ticker": "{homeTicker}", "yes_bid_dollars": "0.6900", "yes_ask_dollars": "0.7100", "result": "" }}"""
    let awayMarket = $"""{{ "ticker": "{awayTicker}", "yes_bid_dollars": "0.2900", "yes_ask_dollars": "0.3100", "result": "" }}"""
    let routes =
        match operation with
        | "listed" -> [ "/trade-api/v2/markets", $"""{{ "cursor": "", "markets": [{homeMarket}, {awayMarket}] }}""" ]
        | "historical" -> [
            $"/trade-api/v2/series/{series}/markets/{homeTicker}/candlesticks",
            """{ "candlesticks": [{ "end_period_ts": 1789331100, "price": { "close_dollars": "0.7000" }, "volume_fp": "100.00" }] }"""
          ]
        | _ -> [
            "/trade-api/v2/markets", """{ "cursor": "", "markets": [] }"""
            $"/trade-api/v2/markets/{homeTicker}", $"""{{ "market": {homeMarket} }}"""
            $"/trade-api/v2/markets/{awayTicker}", $"""{{ "market": {awayMarket} }}"""
          ]
    let service, requests = kalshiServiceWithRoutes (Map.ofList routes)

    match operation with
    | "discovery" ->
        let! ticker = service.FindMarketTickerAsync(sport, home, away, gameDate)
        Assert.Equal(homeTicker, ticker)
        Assert.Single(requests) |> ignore
    | "historical" ->
        let! odds = service.GetHistoricalOddsAsync(sport, home, away, gameDate)
        let sample = Assert.Single(odds)
        Assert.Equal(0.7, sample.HomeWinProbability, 6)
        Assert.Equal(0.3, sample.AwayWinProbability, 6)
    | _ ->
        let! odds = service.GetLiveOddsAsync(sport, home, away, gameDate)
        Assert.NotNull(box odds)
        Assert.Equal(home, odds.HomeTeamAbbr)
        Assert.Equal(away, odds.AwayTeamAbbr)
        Assert.Equal(0.7, odds.HomeWinProbability, 6)
        Assert.Equal(0.3, odds.AwayWinProbability, 6)
        if operation = "listed" then Assert.Single(requests) |> ignore
}

[<Fact>]
let ``GetWorldCupThreeWayLiveOddsAsync parses World Cup home draw away markets`` () = task {
    let json =
        """
        {
          "cursor": "",
          "markets": [
            { "ticker": "KXWCGAME-26JUN19USAAUS-USA", "title": "USA vs Australia Winner?", "status": "open", "yes_bid": 40, "yes_ask": 42, "yes_bid_dollars": "", "yes_ask_dollars": "", "no_bid": null, "no_ask": null, "volume": 100, "close_time": null, "last_price": null, "last_price_dollars": "", "no_sub_title": "USA", "result": "" },
            { "ticker": "KXWCGAME-26JUN19USAAUS-TIE", "title": "USA vs Australia Winner?", "status": "open", "yes_bid": 28, "yes_ask": 30, "yes_bid_dollars": "", "yes_ask_dollars": "", "no_bid": null, "no_ask": null, "volume": 100, "close_time": null, "last_price": null, "last_price_dollars": "", "no_sub_title": "Tie", "result": "" },
            { "ticker": "KXWCGAME-26JUN19USAAUS-AUS", "title": "USA vs Australia Winner?", "status": "open", "yes_bid": 30, "yes_ask": 32, "yes_bid_dollars": "", "yes_ask_dollars": "", "no_bid": null, "no_ask": null, "volume": 100, "close_time": null, "last_price": null, "last_price_dollars": "", "no_sub_title": "Australia", "result": "" }
          ]
        }
        """

    let service = kalshiServiceWithResponse json
    let! odds = service.GetWorldCupThreeWayLiveOddsAsync("USA", "AUS", DateTime(2026, 6, 19, 19, 0, 0, DateTimeKind.Utc))

    Assert.NotNull(box odds)
    Assert.Equal("USA", odds.HomeTeamAbbr)
    Assert.Equal("AUS", odds.AwayTeamAbbr)
    Assert.InRange(odds.HomeWinProbability, 0.40, 0.42)
    Assert.InRange(odds.DrawWinProbability, 0.28, 0.30)
    Assert.InRange(odds.AwayWinProbability, 0.30, 0.32)
    Assert.Equal(1.0, odds.HomeWinProbability + odds.DrawWinProbability + odds.AwayWinProbability, 3)
    Assert.True(odds.IsLive)
}

[<Fact>]
let ``GetWorldCupThreeWayLiveOddsAsync maps ESPN Haiti abbreviation to Kalshi HTI`` () = task {
    let json =
        """
        {
          "cursor": "",
          "markets": [
            { "ticker": "KXWCGAME-26JUN19BRAHTI-BRA", "title": "Brazil vs Haiti Winner?", "status": "open", "yes_bid": 70, "yes_ask": 72, "yes_bid_dollars": "", "yes_ask_dollars": "", "no_bid": null, "no_ask": null, "volume": 100, "close_time": null, "last_price": null, "last_price_dollars": "", "no_sub_title": "Brazil", "result": "" },
            { "ticker": "KXWCGAME-26JUN19BRAHTI-TIE", "title": "Brazil vs Haiti Winner?", "status": "open", "yes_bid": 15, "yes_ask": 17, "yes_bid_dollars": "", "yes_ask_dollars": "", "no_bid": null, "no_ask": null, "volume": 100, "close_time": null, "last_price": null, "last_price_dollars": "", "no_sub_title": "Tie", "result": "" },
            { "ticker": "KXWCGAME-26JUN19BRAHTI-HTI", "title": "Brazil vs Haiti Winner?", "status": "open", "yes_bid": 11, "yes_ask": 13, "yes_bid_dollars": "", "yes_ask_dollars": "", "no_bid": null, "no_ask": null, "volume": 100, "close_time": null, "last_price": null, "last_price_dollars": "", "no_sub_title": "Haiti", "result": "" }
          ]
        }
        """

    let service = kalshiServiceWithResponse json
    let! odds = service.GetWorldCupThreeWayLiveOddsAsync("BRA", "HAI", DateTime(2026, 6, 19, 19, 0, 0, DateTimeKind.Utc))

    Assert.NotNull(box odds)
    Assert.Equal("BRA", odds.HomeTeamAbbr)
    Assert.Equal("HAI", odds.AwayTeamAbbr)
    Assert.True(odds.HomeWinProbability > odds.DrawWinProbability)
    Assert.True(odds.DrawWinProbability > odds.AwayWinProbability)
}

[<Fact>]
let ``GetWorldCupAdvanceLiveOddsAsync parses active knockout advance markets`` () = task {
    let json =
        """
        {
          "cursor": "",
          "markets": [
            { "ticker": "KXWCADVANCE-26JUL11NORENG-NOR", "title": "Norway vs England: To Advance", "status": "open", "yes_bid": null, "yes_ask": null, "yes_bid_dollars": "0.0700", "yes_ask_dollars": "0.0800", "no_bid": null, "no_ask": null, "volume": 100, "close_time": null, "last_price": null, "last_price_dollars": "", "no_sub_title": "Norway advances", "result": "" },
            { "ticker": "KXWCADVANCE-26JUL11NORENG-ENG", "title": "Norway vs England: To Advance", "status": "open", "yes_bid": null, "yes_ask": null, "yes_bid_dollars": "0.9200", "yes_ask_dollars": "0.9300", "no_bid": null, "no_ask": null, "volume": 100, "close_time": null, "last_price": null, "last_price_dollars": "", "no_sub_title": "England advances", "result": "" }
          ]
        }
        """

    let service = kalshiServiceWithResponse json
    let! odds = service.GetWorldCupAdvanceLiveOddsAsync("NOR", "ENG", DateTime(2026, 7, 11, 21, 0, 0, DateTimeKind.Utc))

    Assert.NotNull(box odds)
    Assert.Equal("NOR", odds.HomeTeamAbbr)
    Assert.Equal("ENG", odds.AwayTeamAbbr)
    Assert.InRange(odds.HomeWinProbability, 0.07, 0.08)
    Assert.InRange(odds.AwayWinProbability, 0.92, 0.93)
    Assert.Equal(1.0, odds.HomeWinProbability + odds.AwayWinProbability, 3)
    Assert.True(odds.IsLive)
}
