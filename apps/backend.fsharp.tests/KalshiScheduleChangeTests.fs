module KalshiScheduleChangeTests

open System
open System.IO
open System.Linq
open System.Net
open System.Net.Http
open System.Text.Json.Nodes
open System.Threading.Tasks
open AngleSharp.Html.Parser
open Falco.Markup
open Microsoft.Data.Sqlite
open Microsoft.EntityFrameworkCore
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open backend.Data
open backend.GraphQL.Types
open backend.Services
open backend.Services.Espn
open backend.Services.Kalshi
open Backend.Rendering

let private start = DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc)
let private observed = start.AddHours(1.25)
let private eventTicker = "KXMLBGAME-26OCT081700CLECWS"
let private fixtureFor gameId =
    JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"kalshi-mlb-{gameId}.json")))
let private fixture () = fixtureFor "401907993"
let private advancedStart = DateTime(2026, 10, 12, 21, 0, 0, DateTimeKind.Utc)
let private advancedEventTicker = "KXMLBGAME-26OCT122000LADMIL"

let private response status body =
    let result = new HttpResponseMessage(status)
    result.Content <- new StringContent(body)
    result

let private serviceResponding now scheduleResponse (data: JsonNode) =
    let requests = ResizeArray<Uri>()
    let handler =
        { new HttpMessageHandler() with
            override _.SendAsync(request, _) =
                requests.Add(request.RequestUri)
                let result =
                    match request.RequestUri.AbsolutePath with
                    | "/trade-api/v2/markets" -> response HttpStatusCode.OK (data["catalog"].ToJsonString())
                    | "/trade-api/v2/milestones" -> scheduleResponse ()
                    | _ -> response HttpStatusCode.NotFound "{}"
                Task.FromResult(result) }
    let clock = { new TimeProvider() with override _.GetUtcNow() = DateTimeOffset(now) }
    new KalshiOddsService(new HttpClient(handler), NullLogger<KalshiOddsService>.Instance, clock), requests

let private serviceAt now (data: JsonNode) =
    serviceResponding now (fun () -> response HttpStatusCode.OK (data["schedule"].ToJsonString())) data

let private serviceWith data = serviceAt observed data

[<Fact>]
let ``Synthetic quotes are usable at the original ticker start`` () = task {
    let service, _ = serviceWith (fixture ())
    let! odds = service.GetLiveOddsAsync("mlb", "CHW", "CLE", start.AddHours(-3))
    Assert.NotNull(box odds)
    Assert.Equal(0.625, odds.HomeWinProbability, 6)
    Assert.Equal(0.375, odds.AwayWinProbability, 6)
}

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``Explicit same-day schedule link works in either market order without expiration timestamps`` reverse = task {
    let data = fixture ()
    let markets = (data["catalog"]["markets"]).AsArray()
    for market in markets do
        market.AsObject().Remove("occurrence_datetime") |> ignore
        market.AsObject().Remove("expected_expiration_time") |> ignore
    if reverse then
        let reordered = markets |> Seq.rev |> Seq.map (fun m -> m.DeepClone()) |> Seq.toArray
        markets.Clear()
        for market in reordered do markets.Add(market)
    let service, requests = serviceWith data
    let! odds = service.GetLiveOddsAsync("mlb", "CHW", "CLE", start)
    Assert.NotNull(box odds)
    Assert.Equal(0.625, odds.HomeWinProbability, 6)
    Assert.Equal(2, requests.Count)
    Assert.Equal($"?limit=2&related_event_ticker={eventTicker}", requests[1].Query)
}

[<Theory>]
[<InlineData("missing")>]
[<InlineData("null")>]
[<InlineData("duplicate")>]
[<InlineData("paginated")>]
[<InlineData("wrong start")>]
[<InlineData("missing start")>]
[<InlineData("wrong type")>]
[<InlineData("wrong league")>]
[<InlineData("not live")>]
[<InlineData("suspended")>]
[<InlineData("wrong event")>]
[<InlineData("not primary")>]
[<InlineData("wrong home")>]
[<InlineData("wrong away")>]
[<InlineData("missing details")>]
let ``Schedule evidence must uniquely confirm the started game and both team identities`` scenario = task {
    let data = fixture ()
    let schedule = data["schedule"]
    let milestones = schedule["milestones"].AsArray()
    let milestone = milestones[0]
    let details = milestone["details"]
    match scenario with
    | "missing" -> milestones.Clear()
    | "null" -> schedule["milestones"] <- null
    | "duplicate" -> milestones.Add(milestone.DeepClone())
    | "paginated" -> schedule["cursor"] <- JsonValue.Create("next")
    | "wrong start" -> milestone["start_date"] <- JsonValue.Create("2026-10-08T21:00:00Z")
    | "missing start" -> milestone["start_date"] <- null
    | "wrong type" -> milestone["type"] <- JsonValue.Create("baseball_series")
    | "wrong league" -> details["league"] <- JsonValue.Create("NBA")
    | "not live" -> details["status"] <- JsonValue.Create("scheduled")
    | "suspended" -> details["status"] <- JsonValue.Create("suspended")
    | "wrong event" -> details["main_game_event_ticker"] <- JsonValue.Create("another-event")
    | "not primary" -> milestone["primary_event_tickers"] <- JsonArray()
    | "wrong home" -> details["home_team_id"] <- JsonValue.Create("another-team")
    | "wrong away" -> details["away_team_id"] <- JsonValue.Create("another-team")
    | "missing details" -> milestone["details"] <- null
    | _ -> failwith scenario
    let service, requests = serviceWith data
    let! odds = service.GetLiveOddsAsync("mlb", "CHW", "CLE", start)
    Assert.Null(box odds)
    Assert.Equal(2, requests.Count)
}

[<Theory>]
[<InlineData(0, "closed")>]
[<InlineData(1, "closed")>]
[<InlineData(0, "settled")>]
[<InlineData(1, "settled")>]
[<InlineData(0, "expired")>]
[<InlineData(1, "expired")>]
[<InlineData(0, "missing close")>]
[<InlineData(1, "missing close")>]
[<InlineData(0, "fair price")>]
[<InlineData(1, "fair price")>]
[<InlineData(0, "wrong primary rule")>]
[<InlineData(1, "wrong primary rule")>]
[<InlineData(0, "unknown delay rule")>]
[<InlineData(1, "unknown delay rule")>]
[<InlineData(0, "extra rule")>]
[<InlineData(1, "extra rule")>]
[<InlineData(0, "missing team identity")>]
[<InlineData(1, "missing team identity")>]
[<InlineData(0, "wrong event")>]
[<InlineData(1, "wrong event")>]
[<InlineData(0, "not binary")>]
[<InlineData(1, "not binary")>]
let ``Both outcomes must still be applicable game-winner contracts`` (index: int) scenario = task {
    let data = fixture ()
    let market = data.["catalog"].["markets"].[index]
    match scenario with
    | "closed" -> market["status"] <- JsonValue.Create("closed")
    | "settled" -> market["result"] <- JsonValue.Create("yes")
    | "expired" -> market["close_time"] <- JsonValue.Create(observed.ToString("O"))
    | "missing close" -> market["close_time"] <- null
    | "fair price" -> market["settlement_value_dollars"] <- JsonValue.Create("0.50")
    | "wrong primary rule" -> market["rules_primary"] <- JsonValue.Create("An unrelated game wins")
    | "unknown delay rule" -> market["rules_secondary"] <- JsonValue.Create(market["rules_secondary"].GetValue<string>().Replace("two days", "one hour"))
    | "extra rule" -> market["rules_secondary"] <- JsonValue.Create(market["rules_secondary"].GetValue<string>() + " Different cancellation terms apply.")
    | "missing team identity" -> market["custom_strike"] <- null
    | "wrong event" -> market["event_ticker"] <- JsonValue.Create("another-event")
    | "not binary" -> market["market_type"] <- JsonValue.Create("scalar")
    | _ -> failwith scenario
    let service, requests = serviceWith data
    let! odds = service.GetLiveOddsAsync("mlb", "CHW", "CLE", start)
    Assert.Null(box odds)
    Assert.Single(requests) |> ignore
}

[<Theory>]
[<InlineData("missing home")>]
[<InlineData("missing away")>]
[<InlineData("duplicate outcome")>]
[<InlineData("other event")>]
[<InlineData("doubleheader")>]
[<InlineData("reversed teams")>]
let ``Schedule fallback never guesses between games or incomplete outcomes`` scenario = task {
    let data = fixture ()
    let markets = (data["catalog"]["markets"]).AsArray()
    match scenario with
    | "missing home" -> markets.RemoveAt(0)
    | "missing away" -> markets.RemoveAt(1)
    | "duplicate outcome" -> markets.Add(markets[0].DeepClone())
    | "other event" ->
        for market in markets |> Seq.toArray do
            let other = JsonNode.Parse(market.ToJsonString().Replace("1700", "1900"))
            markets.Add(other)
    | "doubleheader" ->
        for market in markets do
            market["ticker"] <- JsonValue.Create(market["ticker"].GetValue<string>().Replace("CLECWS-", "CLECWSG1-"))
            market["event_ticker"] <- JsonValue.Create(eventTicker + "G1")
    | "reversed teams" ->
        for market in markets do
            market["ticker"] <- JsonValue.Create(market["ticker"].GetValue<string>().Replace("CLECWS", "CWSCLE"))
            market["event_ticker"] <- JsonValue.Create(eventTicker.Replace("CLECWS", "CWSCLE"))
    | _ -> failwith scenario
    let service, requests = serviceWith data
    let! odds = service.GetLiveOddsAsync("mlb", "CHW", "CLE", start)
    Assert.Null(box odds)
    Assert.Single(requests) |> ignore
}

[<Theory>]
[<InlineData(-1.0)>]
[<InlineData(2700.0)>]
[<InlineData(2701.0)>]
let ``Schedule fallback requires live observation before the original 48 hour deadline`` minutes = task {
    let data = fixture ()
    let service, requests = serviceAt (start.AddMinutes(minutes)) data
    let! odds = service.GetLiveOddsAsync("mlb", "CHW", "CLE", start)
    Assert.Null(box odds)
    Assert.Single(requests) |> ignore
}

[<Theory>]
[<InlineData(-24.0)>]
[<InlineData(24.0)>]
let ``Schedule fallback does not infer cross-date makeup games`` hours = task {
    let data = fixture ()
    let changedStart = start.AddHours(hours)
    data.["schedule"].["milestones"].[0].["start_date"] <- JsonValue.Create(changedStart.ToString("O"))
    let service, requests = serviceAt (changedStart.AddHours(1)) data
    let! odds = service.GetLiveOddsAsync("mlb", "CHW", "CLE", changedStart)
    Assert.Null(box odds)
    Assert.DoesNotContain(requests, fun uri -> uri.AbsolutePath = "/trade-api/v2/milestones")
}

[<Theory>]
[<InlineData(200, "{")>]
[<InlineData(200, "null")>]
[<InlineData(200, "{}")>]
[<InlineData(404, "{}")>]
[<InlineData(500, "{}")>]
let ``Unavailable or malformed schedule evidence fails closed`` status body = task {
    let service, requests = serviceResponding observed (fun () -> response (enum<HttpStatusCode> status) body) (fixture ())
    let! odds = service.GetLiveOddsAsync("mlb", "CHW", "CLE", start)
    Assert.Null(box odds)
    Assert.Equal(2, requests.Count)
}

[<Fact>]
let ``Schedule fallback does not change pregame or other sports matching`` () = task {
    let data = fixture ()
    let service, requests = serviceWith data
    let! pregame = service.GetPregameOddsAsync("mlb", "CHW", "CLE", start, start.AddHours(-1))
    Assert.Null(box pregame)
    Assert.Single(requests) |> ignore
    let basketball = JsonNode.Parse(data.ToJsonString().Replace("KXMLBGAME", "KXNBAGAME"))
    let other, otherRequests = serviceWith basketball
    let! nba = other.GetLiveOddsAsync("nba", "CHW", "CLE", start)
    Assert.Null(box nba)
    Assert.Single(otherRequests) |> ignore
}

[<Theory>]
[<InlineData("401907993", 0.625, 0.375)>]
[<InlineData("401908031", 0.825, 0.175)>]
let ``Schedule changed games display live probabilities through the ESPN scraper and stored history`` gameId homeProbability awayProbability = task {
    let data = fixtureFor gameId
    let currentStart = DateTime.Parse(data.["scoreboard"].["events"].[0].["date"].GetValue<string>(), Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.RoundtripKind)
    let now = currentStart.AddHours(1.25)
    use connection = new SqliteConnection("Data Source=:memory:")
    connection.Open()
    let services = ServiceCollection()
    let clock = { new TimeProvider() with override _.GetUtcNow() = DateTimeOffset(now) }
    services.AddSingleton<TimeProvider>(clock) |> ignore
    services.AddDbContext<ExcitementDbContext>(fun options -> options.UseSqlite(connection) |> ignore) |> ignore
    use provider = services.BuildServiceProvider()
    use scope = provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    let! _ = db.Database.EnsureCreatedAsync()
    let mutable polls = 0
    use handler =
        { new HttpMessageHandler() with
            override _.SendAsync(_, _) =
                polls <- polls + 1
                let body = if polls = 1 then data["scoreboard"].ToJsonString() else "{\"events\":[]}"
                Task.FromResult(response HttpStatusCode.OK body) }
    use http = new HttpClient(handler)
    let published = ResizeArray<GameUpdate>()
    let updates =
        { new IGameUpdateService with
            member _.PublishUpdateAsync(update, _) =
                published.Add(update)
                Task.CompletedTask }
    let kalshi, requests = serviceAt now data
    let scraper = EspnOddsService(http, NullLogger<EspnOddsService>.Instance, provider, updates, kalshi)
    let! _ = scraper.ScrapeAndStoreOddsAsync("mlb")
    let! game = db.Games.SingleAsync(fun g -> g.Id = gameId)
    let! snapshots = db.OddsSnapshots.Where(fun s -> s.GameId = game.Id).OrderBy(fun s -> s.Timestamp).ToListAsync()
    let html =
        GameDetailRenderer.RenderAtTime now game snapshots None Unchecked.defaultof<HypeComponents> (Nullable()) None None
        |> List.map XmlNodeRenderer.renderNode |> String.concat ""
    use doc = HtmlParser().ParseDocument(html)

    Assert.Equal("Live", doc.QuerySelector("#detail-status").TextContent)
    Assert.Contains("%", doc.QuerySelector("#detail-probbar").TextContent)
    let snapshot = Assert.Single(snapshots)
    Assert.Equal(homeProbability, snapshot.HomeWinProbability, 6)
    Assert.Equal(awayProbability, snapshot.AwayWinProbability, 6)
    Assert.Equal("Kalshi", snapshot.Source)
    Assert.False(snapshot.IsHistorical)
    Assert.Equal(now, snapshot.Timestamp)
    Assert.Equal(currentStart, game.ScheduledStart)
    Assert.Equal(Nullable homeProbability, Assert.Single(published).HomeWinProbability)
    let selectedEventTicker = data.["catalog"].["markets"].[0].["event_ticker"].GetValue<string>()
    Assert.Contains(requests, fun uri -> uri.AbsolutePath = "/trade-api/v2/milestones" && uri.Query.Contains(selectedEventTicker))
}

[<Fact>]
let ``Game 401908031 synthetic quotes are usable at the original ticker start`` () = task {
    let service, requests = serviceAt (advancedStart.AddHours(1.25)) (fixtureFor "401908031")
    let! odds = service.GetLiveOddsAsync("mlb", "MIL", "LAD", advancedStart.AddHours(3))
    Assert.NotNull(box odds)
    Assert.Equal(0.825, odds.HomeWinProbability, 6)
    Assert.Equal(0.175, odds.AwayWinProbability, 6)
    Assert.Single(requests) |> ignore
}

[<Theory>]
[<InlineData(-4.0)>]
[<InlineData(0.0)>]
let ``Explicit time rules support verified earlier and later same-day starts`` hours = task {
    let data = fixture ()
    let changedStart = start.AddHours(hours)
    data.["schedule"].["milestones"].[0].["start_date"] <- JsonValue.Create(changedStart.ToString("O"))
    let service, requests = serviceAt (changedStart.AddMinutes(30)) data
    let! odds = service.GetLiveOddsAsync("mlb", "CHW", "CLE", changedStart)
    Assert.NotNull(box odds)
    Assert.Equal(0.625, odds.HomeWinProbability, 6)
    Assert.Equal(2, requests.Count)
}

[<Theory>]
[<InlineData(0.0, false)>]
[<InlineData(0.0, true)>]
[<InlineData(4.0, false)>]
[<InlineData(4.0, true)>]
let ``Date only rules support verified earlier and later starts regardless of market order`` hours reverse = task {
    let data = fixtureFor "401908031"
    let changedStart = advancedStart.AddHours(hours)
    data.["schedule"].["milestones"].[0].["start_date"] <- JsonValue.Create(changedStart.ToString("O"))
    let markets = data.["catalog"].["markets"].AsArray()
    if reverse then
        let reordered = markets |> Seq.rev |> Seq.map (fun m -> m.DeepClone()) |> Seq.toArray
        markets.Clear()
        for market in reordered do markets.Add(market)
    let service, requests = serviceAt (changedStart.AddMinutes(30)) data
    let! odds = service.GetLiveOddsAsync("mlb", "MIL", "LAD", changedStart)
    Assert.NotNull(box odds)
    Assert.Equal(0.825, odds.HomeWinProbability, 6)
    Assert.Equal(0.175, odds.AwayWinProbability, 6)
    Assert.Equal(2, requests.Count)
    Assert.Equal($"?limit=2&related_event_ticker={advancedEventTicker}", requests[1].Query)
}

[<Theory>]
[<InlineData("wrong start")>]
[<InlineData("wrong event")>]
[<InlineData("not primary")>]
[<InlineData("reversed home away")>]
[<InlineData("not live")>]
[<InlineData("duplicate milestone")>]
[<InlineData("paginated")>]
[<InlineData("missing schedule")>]
let ``Earlier starts with date only rules still require unique current schedule evidence`` scenario = task {
    let data = fixtureFor "401908031"
    let schedule = data["schedule"]
    let milestones = schedule["milestones"].AsArray()
    let milestone = milestones[0]
    let details = milestone["details"]
    match scenario with
    | "wrong start" -> milestone["start_date"] <- JsonValue.Create(advancedStart.AddHours(3).ToString("O"))
    | "wrong event" -> details["main_game_event_ticker"] <- JsonValue.Create("another-event")
    | "not primary" -> milestone["primary_event_tickers"] <- JsonArray()
    | "reversed home away" ->
        let home = details["home_team_id"].DeepClone()
        details["home_team_id"] <- details["away_team_id"].DeepClone()
        details["away_team_id"] <- home
    | "not live" -> details["status"] <- JsonValue.Create("scheduled")
    | "duplicate milestone" -> milestones.Add(milestone.DeepClone())
    | "paginated" -> schedule["cursor"] <- JsonValue.Create("next")
    | "missing schedule" -> milestones.Clear()
    | _ -> failwith scenario
    let service, requests = serviceAt (advancedStart.AddMinutes(30)) data
    let! odds = service.GetLiveOddsAsync("mlb", "MIL", "LAD", advancedStart)
    Assert.Null(box odds)
    Assert.Equal(2, requests.Count)
}

[<Theory>]
[<InlineData(0, "closed")>]
[<InlineData(1, "closed")>]
[<InlineData(0, "settled")>]
[<InlineData(1, "settled")>]
[<InlineData(0, "fair price")>]
[<InlineData(1, "fair price")>]
[<InlineData(0, "wrong date")>]
[<InlineData(1, "wrong date")>]
[<InlineData(0, "wrong time")>]
[<InlineData(1, "wrong time")>]
[<InlineData(0, "mixed rule formats")>]
[<InlineData(1, "mixed rule formats")>]
[<InlineData(0, "unknown rules")>]
[<InlineData(1, "unknown rules")>]
let ``Date only reconciliation checks both applicable contracts with one exact known rule format`` (index: int) scenario = task {
    let data = fixtureFor "401908031"
    let market = data.["catalog"].["markets"].[index]
    let replaceRules replacement =
        for field in ["rules_primary"; "rules_secondary"] do
            market[field] <- JsonValue.Create(market[field].GetValue<string>().Replace("Oct 12, 2026", replacement))
    match scenario with
    | "closed" -> market["status"] <- JsonValue.Create("closed")
    | "settled" -> market["result"] <- JsonValue.Create("yes")
    | "fair price" -> market["settlement_value_dollars"] <- JsonValue.Create("0.50")
    | "wrong date" -> replaceRules "Oct 11, 2026"
    | "wrong time" -> replaceRules "Oct 12, 2026 at 5:00 PM EDT"
    | "mixed rule formats" -> replaceRules "Oct 12, 2026 at 8:00 PM EDT"
    | "unknown rules" -> market["rules_secondary"] <- JsonValue.Create(market["rules_secondary"].GetValue<string>() + " Different cancellation terms apply.")
    | _ -> failwith scenario
    let service, requests = serviceAt (advancedStart.AddMinutes(30)) data
    let! odds = service.GetLiveOddsAsync("mlb", "MIL", "LAD", advancedStart)
    Assert.Null(box odds)
    Assert.Single(requests) |> ignore
}
