module KalshiDoubleheaderTests

open System
open System.Linq
open System.Net
open System.Net.Http
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Microsoft.EntityFrameworkCore
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open backend.Data
open backend.Services
open backend.Services.Espn
open backend.Services.Kalshi

let private game1 = "KXMLBGAME-26SEP221305TBNYYG1"
let private game2 = "KXMLBGAME-26SEP221905TBNYYG2"
let private game1Start = DateTime(2026, 9, 22, 17, 5, 0, DateTimeKind.Utc)
let private game2Start = game1Start.AddHours(6)

let private market eventTicker team price result =
    $"""{{"ticker":"{eventTicker}-{team}","event_ticker":"{eventTicker}","yes_bid_dollars":"{price}","yes_ask_dollars":"{price}","result":"{result}"}}"""

let private pair eventTicker =
    [ market eventTicker "NYY" "0.70" ""; market eventTicker "TB" "0.30" "" ]

let private doubleheader =
    [ market game2 "TB" "0.80" ""
      market game1 "NYY" "0.70" ""
      market game2 "NYY" "0.20" ""
      market game1 "TB" "0.30" "" ]

let private response status json =
    let result = new HttpResponseMessage(status)
    result.Content <- new StringContent(json)
    result

let private serviceWithMarkets markets =
    let requests = ResizeArray<Uri>()
    let json = "{\"cursor\":\"\",\"markets\":[" + String.concat "," markets + "]}"
    let handler =
        { new HttpMessageHandler() with
            override _.SendAsync(request, _ct) =
                requests.Add(request.RequestUri)
                let result =
                    if request.RequestUri.AbsolutePath = "/trade-api/v2/markets" then
                        response HttpStatusCode.OK json
                    else response HttpStatusCode.NotFound "{}"
                Task.FromResult(result) }
    new KalshiOddsService(new HttpClient(handler), NullLogger<KalshiOddsService>.Instance), requests

[<Theory>]
[<InlineData(1, false)>]
[<InlineData(1, true)>]
[<InlineData(2, false)>]
[<InlineData(2, true)>]
let ``Live doubleheader odds belong to the scheduled game regardless of list order`` gameNumber reverse = task {
    let markets = if reverse then List.rev doubleheader else doubleheader
    let service, requests = serviceWithMarkets markets
    let start = if gameNumber = 1 then game1Start else game2Start
    let expected = if gameNumber = 1 then 0.7 else 0.2

    let! odds = service.GetLiveOddsAsync("mlb", "NYY", "TB", start)

    Assert.NotNull(box odds)
    Assert.Equal(expected, odds.HomeWinProbability, 6)
    Assert.Equal(1.0 - expected, odds.AwayWinProbability, 6)
    Assert.Single(requests) |> ignore
}

[<Fact>]
let ``Game two still resolves after game one settles`` () = task {
    let markets =
        [ market game1 "NYY" "1.00" "yes"; market game1 "TB" "0.00" "no" ] @ pair game2
    let service, requests = serviceWithMarkets markets
    let! odds = service.GetLiveOddsAsync("mlb", "NYY", "TB", game2Start)
    Assert.NotNull(box odds)
    Assert.Equal(0.7, odds.HomeWinProbability, 6)
    Assert.Single(requests) |> ignore
}

[<Fact>]
let ``Doubleheader date matching is independent of server culture`` () = task {
    let original = Globalization.CultureInfo.CurrentCulture
    try
        Globalization.CultureInfo.CurrentCulture <- Globalization.CultureInfo("ar-SA")
        let service, _ = serviceWithMarkets doubleheader
        let! odds = service.GetLiveOddsAsync("mlb", "NYY", "TB", game1Start)
        Assert.NotNull(box odds)
        Assert.Equal(0.7, odds.HomeWinProbability, 6)
    finally
        Globalization.CultureInfo.CurrentCulture <- original
}

[<Theory>]
[<InlineData("KXMLBGAME-26SEP222305TBNYYG2")>]
[<InlineData("KXMLBGAME-26SEP222305NYYTBG2")>]
let ``Doubleheader start uses Eastern date and time including reversed team order`` eventTicker = task {
    let service, _ = serviceWithMarkets (pair eventTicker)
    let! odds = service.GetLiveOddsAsync("mlb", "NYY", "TB", DateTime(2026, 9, 23, 3, 5, 0, DateTimeKind.Utc))
    Assert.NotNull(box odds)
    Assert.Equal(0.7, odds.HomeWinProbability, 6)
}

[<Theory>]
[<InlineData("KXMLBGAME-26SEP221305TBNYY")>]
[<InlineData("KXMLBGAME-26SEP22TBNYY")>]
let ``Live single-game odds still accept timed and legacy tickers`` eventTicker = task {
    let service, _ = serviceWithMarkets (pair eventTicker)
    let! odds = service.GetLiveOddsAsync("mlb", "NYY", "TB", game1Start)
    Assert.NotNull(box odds)
    Assert.Equal(0.7, odds.HomeWinProbability, 6)
}

[<Theory>]
[<InlineData("other game only")>]
[<InlineData("missing home outcome")>]
[<InlineData("missing away outcome")>]
[<InlineData("settled game")>]
[<InlineData("settled away outcome")>]
[<InlineData("same start ambiguous")>]
[<InlineData("untimed doubleheader")>]
[<InlineData("timed games without suffix")>]
let ``Live doubleheader matching fails closed instead of borrowing another event`` scenario = task {
    let markets =
        match scenario with
        | "other game only" -> pair game2
        | "missing home outcome" -> market game1 "TB" "0.30" "" :: pair game2
        | "missing away outcome" -> market game1 "NYY" "0.70" "" :: pair game2
        | "settled game" ->
            [ market game1 "NYY" "1.00" "yes"; market game1 "TB" "0.00" "no" ] @ pair game2
        | "settled away outcome" ->
            [ market game1 "NYY" "0.70" ""; market game1 "TB" "0.30" "no" ] @ pair game2
        | "same start ambiguous" -> pair game1 @ pair "KXMLBGAME-26SEP221305TBNYYG2"
        | "untimed doubleheader" -> pair "KXMLBGAME-26SEP22TBNYYG1" @ pair "KXMLBGAME-26SEP22TBNYYG2"
        | "timed games without suffix" -> pair "KXMLBGAME-26SEP221905TBNYY"
        | _ -> failwith "Unknown scenario"
    let service, requests = serviceWithMarkets markets

    let! odds = service.GetLiveOddsAsync("mlb", "NYY", "TB", game1Start)

    Assert.Null(box odds)
    Assert.Single(requests) |> ignore
}

[<Fact>]
let ``Doubleheader schedule mismatch does not guess the nearest game`` () = task {
    let service, requests = serviceWithMarkets doubleheader
    let! odds = service.GetLiveOddsAsync("mlb", "NYY", "TB", game1Start.AddMinutes(5))
    Assert.Null(box odds)
    Assert.Single(requests) |> ignore
}

[<Theory>]
[<InlineData(1)>]
[<InlineData(2)>]
let ``Ticker discovery selects the same doubleheader event as live odds`` gameNumber = task {
    let service, _ = serviceWithMarkets doubleheader
    let start, expected = if gameNumber = 1 then game1Start, game1 else game2Start, game2
    let! ticker = service.FindMarketTickerAsync("mlb", "NYY", "TB", start)
    Assert.Equal(expected + "-NYY", ticker)
}

[<Fact>]
let ``Ticker discovery does not substitute another date or start time`` () = task {
    let service, _ = serviceWithMarkets doubleheader
    let! wrongTime = service.FindMarketTickerAsync("mlb", "NYY", "TB", game1Start.AddMinutes(5))
    let! wrongDate = service.FindMarketTickerAsync("mlb", "NYY", "TB", game1Start.AddDays(1))
    Assert.Null(wrongTime)
    Assert.Null(wrongDate)
}

[<Fact>]
let ``ESPN scraper persists separate live odds histories for both doubleheader games`` () = task {
    use connection = new SqliteConnection("Data Source=:memory:")
    connection.Open()
    let services = ServiceCollection()
    services.AddDbContext<ExcitementDbContext>(fun options -> options.UseSqlite(connection) |> ignore) |> ignore
    use provider = services.BuildServiceProvider()
    use scope = provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    let! _ = db.Database.EnsureCreatedAsync()

    let event id start =
        $"""{{"id":"{id}","shortName":"TB @ NYY","date":"{start}",
          "status":{{"type":{{"state":"in"}},"period":3,"displayClock":""}},
          "competitions":[{{"competitors":[
            {{"homeAway":"home","score":"2","team":{{"abbreviation":"NYY","displayName":"New York Yankees"}}}},
            {{"homeAway":"away","score":"0","team":{{"abbreviation":"TB","displayName":"Tampa Bay Rays"}}}}
          ]}}]}}"""
    let events = [ event "401873648" "2026-09-22T17:05:00Z"; event "401817034" "2026-09-22T23:05:00Z" ]
    let mutable polls = 0
    use handler =
        { new HttpMessageHandler() with
            override _.SendAsync(_request, _ct) =
                polls <- polls + 1
                let json = "{\"events\":[" + (if polls = 1 then String.concat "," events else "") + "]}"
                Task.FromResult(response HttpStatusCode.OK json) }
    use http = new HttpClient(handler)
    let updates =
        { new IGameUpdateService with
            member _.PublishUpdateAsync(_update, _ct) = Task.CompletedTask }
    let kalshi, _ = serviceWithMarkets doubleheader
    let scraper = EspnOddsService(http, NullLogger<EspnOddsService>.Instance, provider, updates, kalshi)

    let! snapshots = scraper.ScrapeAndStoreOddsAsync("mlb")

    Assert.Equal(2, snapshots.Count)
    let! first = db.OddsSnapshots.Where(fun o -> o.GameId = "401873648").ToListAsync()
    let! second = db.OddsSnapshots.Where(fun o -> o.GameId = "401817034").ToListAsync()
    Assert.InRange(Assert.Single(first).HomeWinProbability, 0.69, 0.71)
    Assert.InRange(Assert.Single(second).HomeWinProbability, 0.19, 0.21)
    Assert.Equal("Kalshi", Assert.Single(first).Source)
    Assert.Equal("Kalshi", Assert.Single(second).Source)
}
