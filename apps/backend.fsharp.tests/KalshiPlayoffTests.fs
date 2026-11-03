module KalshiPlayoffTests

open System
open System.IO
open System.Linq
open System.Net
open System.Net.Http
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Microsoft.EntityFrameworkCore
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open backend.Data
open backend.Models
open backend.Services.Kalshi
open backend.Services
open Backend.Rendering
open Falco.Markup

let private scenarioTime = DateTimeOffset(2026, 9, 14, 23, 30, 0, TimeSpan.Zero)

let private fixture name =
    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name))

let private jsonResponse status json =
    let response = new HttpResponseMessage(status)
    response.Content <- new StringContent(json)
    response

let private liveFixture sport =
    match sport with
    | "nfl" -> "kalshi-nfl-playoffs-2026.json"
    | "nba" -> "kalshi-nba-playoffs-2027.json"
    | "nhl" -> "kalshi-nhl-playoffs-2027.json"
    | "mlb" -> "kalshi-mlb-playoffs-2026.json"
    | _ -> failwith "Unsupported test sport"

let private eventWithQuotes sport =
    let root = JsonNode.Parse(fixture (liveFixture sport))
    for market in (root["event"]["markets"]).AsArray() do
        market["yes_bid_dollars"] <- JsonValue.Create("0.6000")
        market["yes_ask_dollars"] <- JsonValue.Create("0.6400")
        market["yes_bid_size_fp"] <- JsonValue.Create("10.00")
        market["yes_ask_size_fp"] <- JsonValue.Create("10.00")
    root

let private market (root: JsonNode) suffix =
    (root["event"]["markets"]).AsArray()
    |> Seq.find (fun m -> (m["ticker"]).GetValue<string>().EndsWith("-" + suffix, StringComparison.Ordinal))

type private Clock() =
    inherit TimeProvider()
    member val Now = scenarioTime with get, set
    override this.GetUtcNow() = this.Now

type private Importer() =
    let connection = new SqliteConnection("Data Source=:memory:")
    let services = ServiceCollection()
    do
        connection.Open()
        services.AddDbContext<ExcitementDbContext>(fun options -> options.UseSqlite(connection) |> ignore) |> ignore
    let provider = services.BuildServiceProvider()
    let clock = Clock()
    let requests = ResizeArray<Uri>()
    let mutable respond: HttpRequestMessage -> CancellationToken -> Task<HttpResponseMessage> =
        fun _ _ -> Task.FromResult(jsonResponse HttpStatusCode.OK "{\"markets\":[],\"cursor\":\"\"}")
    let handler =
        { new HttpMessageHandler() with
            override _.SendAsync(request, ct) =
                requests.Add(request.RequestUri)
                respond request ct }
    let http = new HttpClient(handler)
    let service = KalshiPlayoffRatingsService(http, NullLogger<KalshiPlayoffRatingsService>.Instance, provider, clock)
    do
        use scope = provider.CreateScope()
        scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.EnsureCreated() |> ignore

    member _.Clock = clock
    member _.Provider = provider
    member _.Requests = requests
    member _.Service = service
    member _.Respond with set value = respond <- value
    member this.Event(root: JsonNode) =
        this.Respond <- fun request _ ->
            let body = if request.RequestUri.AbsolutePath.Contains("/events/") then root.ToJsonString() else "{\"markets\":[],\"cursor\":\"\"}"
            Task.FromResult(jsonResponse HttpStatusCode.OK body)
    member _.Refresh(sport, season) = service.RefreshAsync(sport, season, CancellationToken.None)
    member _.Rows(sport, season) = task {
        use scope = provider.CreateScope()
        let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
        return! db.TeamRatings.AsNoTracking().Where(fun r -> r.Sport = sport && r.Season = season).ToListAsync()
    }
    member _.CollegeTeams(teams: string list) = task {
        use scope = provider.CreateScope()
        let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
        for index, abbreviation in List.indexed teams do
            db.CollegeTeams.Add(CollegeTeam(Season = 2026, EspnId = string index, Abbreviation = abbreviation, Name = abbreviation)) |> ignore
        let! _ = db.SaveChangesAsync()
        return ()
    }
    member _.Seed(row: TeamRating) = task {
        use scope = provider.CreateScope()
        let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
        db.TeamRatings.Add(row) |> ignore
        let! _ = db.SaveChangesAsync()
        return ()
    }
    interface IDisposable with
        member _.Dispose() =
            http.Dispose()
            provider.Dispose()
            connection.Dispose()

let private expectedTeams = function
    | "nfl" -> "ARI ATL BAL BUF CAR CHI CIN CLE DAL DEN DET GB HOU IND JAX KC LAC LAR LV MIA MIN NE NO NYG NYJ PHI PIT SEA SF TB TEN WSH"
    | "nba" -> "ATL BOS BKN CHA CHI CLE DAL DEN DET GS HOU IND LAC LAL MEM MIA MIL MIN NO NY OKC ORL PHI PHX POR SAC SA TOR UTAH WSH"
    | "nhl" -> "ANA BOS BUF CGY CAR CHI COL CBJ DAL DET EDM FLA LA MIN MTL NSH NJ NYI NYR OTT PHI PIT SJ SEA STL TB TOR UTAH VAN VGK WSH WPG"
    | "mlb" -> "ARI ATH ATL BAL BOS CHC CHW CIN CLE COL DET HOU KC LAA LAD MIA MIL MIN NYM NYY PHI PIT SD SF SEA STL TB TEX TOR WSH"
    | _ -> failwith "Unsupported test sport"

[<Theory>]
[<InlineData("nfl", 2026, 32)>]
[<InlineData("nba", 2027, 30)>]
[<InlineData("nhl", 2027, 32)>]
[<InlineData("mlb", 2026, 30)>]
let ``Qualification events import every ESPN team without normalization`` sport season expectedCount = task {
    use importer = new Importer()
    importer.Event(eventWithQuotes sport)
    let! count = importer.Refresh(sport, season)
    Assert.Equal(expectedCount, count)
    let! rows = importer.Rows(sport, season)
    Assert.Equal(expectedCount, rows.Count)
    let expected = (expectedTeams sport).Split(' ') |> Set.ofArray
    Assert.Equal<Set<string>>(expected, rows |> Seq.map (fun r -> r.TeamAbbreviation) |> Set.ofSeq)
    Assert.All(rows, fun r ->
        Assert.Equal(Nullable 0.62, r.PlayoffProbability)
        Assert.False(r.PlayoffProbabilityIsFinal)
        Assert.Equal(Nullable season, r.SourceSeason)
        Assert.Equal("kalshi", r.Source)
        Assert.Equal(scenarioTime.UtcDateTime, r.FetchedAt))
    let! again = importer.Refresh(sport, season)
    let! after = importer.Rows(sport, season)
    Assert.Equal(expectedCount, again)
    Assert.Equal(expectedCount, after.Count)
}

let private set (node: JsonNode) (field: string) (value: string) = node[field] <- JsonValue.Create(value)

let private removeMarket (root: JsonNode) (suffix: string) =
    (root["event"]["markets"]).AsArray().Remove(market root suffix) |> ignore

let private archiveResponse (markets: JsonNode list) cursor =
    let root = JsonNode.Parse("{\"markets\":[],\"cursor\":\"\"}")
    for market in markets do (root["markets"]).AsArray().Add(market.DeepClone())
    set root "cursor" cursor
    root.ToJsonString()

let private withArchive (importer: Importer) (event: JsonNode) status archive =
    importer.Respond <- fun request _ ->
        if request.RequestUri.AbsolutePath.Contains("/historical/") then
            Task.FromResult(jsonResponse status archive)
        else Task.FromResult(jsonResponse HttpStatusCode.OK (event.ToJsonString()))

let private finalMarket (node: JsonNode) result value =
    set node "status" "finalized"
    set node "result" result
    set node "settlement_value_dollars" value
    node["yes_bid_dollars"] <- null
    node["yes_ask_dollars"] <- null
    node["yes_bid_size_fp"] <- null
    node["yes_ask_size_fp"] <- null
    set node "close_time" "2026-09-01T00:00:00Z"

let private nbaRating team probability isFinal fetchedAt =
    TeamRating(Sport = "nba", Season = 2027, Source = "kalshi", SourceSeason = Nullable 2027,
               TeamAbbreviation = team, PlayoffProbability = probability,
               PlayoffProbabilityIsFinal = isFinal, FetchedAt = fetchedAt)

[<Theory>]
[<InlineData("nba", 2027, 30, "BOS", 0.74)>]
[<InlineData("nhl", 2027, 32, "ANA", 0.44)>]
[<InlineData("mlb", 2026, 30, "SEA", 0.14)>]
let ``Constructed snapshots retain their specified quotes and per-team availability`` sport season expectedCount team expected = task {
    use importer = new Importer()
    importer.Event(JsonNode.Parse(fixture (liveFixture sport)))
    let! count = importer.Refresh(sport, season)
    Assert.Equal(expectedCount, count)
    let! rows = importer.Rows(sport, season)
    let row = rows |> Seq.find (fun r -> r.TeamAbbreviation = team)
    Assert.Equal(expected, row.PlayoffProbability.Value, 6)
    if sport = "mlb" then
        for team, expected in ["ATL", 0.995; "MIL", 0.995; "COL", 0.005; "CIN", 0.005] do
            let extreme = rows |> Seq.find (fun r -> r.TeamAbbreviation = team)
            Assert.Equal(Nullable expected, extreme.PlayoffProbability)
            Assert.False(extreme.PlayoffProbabilityIsFinal)
}

[<Fact>]
let ``Synthetic ATL and CIN one-sided quotes render bounds on every playoff surface and expire`` () = task {
    use importer = new Importer()
    let root = JsonNode.Parse(fixture (liveFixture "mlb"))
    importer.Event(root)
    let! _ = importer.Refresh("mlb", 2026)
    use scope = importer.Provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    let data = GameDataService(db, importer.Clock)
    let info team = data.GetTeamRankInfoAsync("mlb", team, Nullable 2026, Nullable 2, CancellationToken.None)
    let! home = info "ATL"
    let! away = info "CIN"
    Assert.Equal(Some ">99% playoff odds", home.Value.DisplayRank)
    Assert.Equal(Some "<1% playoff odds", away.Value.DisplayRank)
    Assert.False(home.Value.PlayoffIsFinal)
    Assert.False(away.Value.PlayoffIsFinal)

    let game = Game(Id = "extreme-quotes", Sport = "mlb", HomeTeam = "Atlanta", AwayTeam = "Cincinnati",
                    HomeTeamAbbreviation = "ATL", AwayTeamAbbreviation = "CIN", Status = "scheduled",
                    SeasonYear = Nullable 2026, SeasonType = Nullable 2, ScheduledStart = scenarioTime.UtcDateTime)
    let! rows = importer.Rows("mlb", 2026)
    let row team = rows |> Seq.find (fun r -> r.TeamAbbreviation = team)
    let hype = HypeScoreService(importer.Provider, NullLogger<HypeScoreService>.Instance, importer.Clock)
    let odds = PregameOdds(GameId = game.Id, HomeWinProbability = 0.5, AwayWinProbability = 0.5)
    let components = hype.CalculateFromData(game, row "ATL", row "CIN", odds, null, null)
    Assert.Equal(Nullable 0.995, components.HomePlayoffOdds)
    Assert.Equal(Nullable 0.005, components.AwayPlayoffOdds)
    let! ranks = data.GetTeamRankInfosAsync("mlb", ["ATL"; "CIN"], game.SeasonYear, game.SeasonType, CancellationToken.None)
    let keyedRanks = Collections.Generic.Dictionary<string, TeamRankInfo>()
    for entry in ranks do keyedRanks[$"mlb:2026:2:{entry.Key}"] <- entry.Value
    let games = ResizeArray<Game>([game])
    let emptyOdds = Collections.Generic.Dictionary<string, OddsSnapshot>()
    let emptyScores = Collections.Generic.Dictionary<string, Nullable<float>>()
    let render nodes = nodes |> List.map XmlNodeRenderer.renderNode |> String.concat ""
    for nodes in [
        HomePageRenderer.Render games emptyOdds emptyScores emptyScores keyedRanks
        LeaguePageRenderer.Render backend.GraphQL.Types.Sport.MLB (LeagueConfig.getLeagueConfig backend.GraphQL.Types.Sport.MLB) games emptyOdds emptyScores emptyScores None keyedRanks None None None
    ] do
        let html = render nodes
        Assert.Contains("&gt;99% playoff odds", html)
        Assert.Contains("&lt;1% playoff odds", html)
        Assert.DoesNotContain("Playoff odds unavailable", html)
    let detail = GameDetailRenderer.Render game (ResizeArray<OddsSnapshot>()) None components (Nullable()) home away |> render
    Assert.Contains("&gt;99%", detail)
    Assert.Contains("&lt;1%", detail)
    Assert.Contains("Playoff outlook", detail)
    Assert.DoesNotContain("Playoff Odds", detail)
    Assert.DoesNotContain("Final outcome", detail)
    Assert.DoesNotContain("Clinched playoffs", detail)
    Assert.DoesNotContain("Missed playoffs", detail)

    importer.Clock.Now <- scenarioTime.AddHours(1.0)
    let! expired = info "ATL"
    Assert.True(expired.Value.PlayoffOdds.IsNone)
    Assert.Equal(Some "Playoff odds unavailable", expired.Value.DisplayRank)
    let expiredComponents = hype.CalculateFromData(game, row "ATL", row "CIN", odds, null, null)
    Assert.False(expiredComponents.HomePlayoffOdds.HasValue)
    Assert.False(expiredComponents.AwayPlayoffOdds.HasValue)
    Assert.False(expiredComponents.StakesAvailable)

    finalMarket (market root "ATL") "yes" "1.0000"
    finalMarket (market root "CIN") "no" "0.0000"
    let! _ = importer.Refresh("mlb", 2026)
    db.ChangeTracker.Clear()
    importer.Clock.Now <- scenarioTime.AddDays(2.0)
    let! qualified = info "ATL"
    let! eliminated = info "CIN"
    Assert.True(qualified.Value.PlayoffIsFinal)
    Assert.True(eliminated.Value.PlayoffIsFinal)
    Assert.Equal(Some 1.0, qualified.Value.PlayoffOdds)
    Assert.Equal(Some 0.0, eliminated.Value.PlayoffOdds)
    Assert.Equal(Some "Clinched playoffs", qualified.Value.DisplayRank)
    Assert.Equal(Some "Missed playoffs", eliminated.Value.DisplayRank)
    let finalDetail = GameDetailRenderer.Render game (ResizeArray<OddsSnapshot>()) None components (Nullable()) qualified eliminated |> render
    Assert.Contains("Playoff outlook", finalDetail)
    Assert.Contains("Clinched playoffs", finalDetail)
    Assert.Contains("Missed playoffs", finalDetail)
    Assert.DoesNotContain("Final outcome", finalDetail)
    Assert.DoesNotContain("Playoff Odds", finalDetail)
    Assert.DoesNotContain("Qualified", finalDetail)
    Assert.DoesNotContain("Did not qualify", finalDetail)
    Assert.DoesNotContain("&gt;99%", finalDetail)
    Assert.DoesNotContain("&lt;1%", finalDetail)
}

[<Theory>]
[<InlineData("0.9900", "1.0000", "0.01", "0.00", 0.995)>]
[<InlineData("0.9990", "1.0000", "1.00", "0.00", 0.9995)>]
[<InlineData("0.0000", "0.0100", "0.00", "0.01", 0.005)>]
[<InlineData("0.0000", "0.0010", "0.00", "1.00", 0.0005)>]
let ``Only populated extreme side is needed for a nonfinal estimate`` bid ask bidSize askSize expected = task {
    use importer = new Importer()
    let root = eventWithQuotes "mlb"
    let node = market root "ATL"
    for field, value in ["yes_bid_dollars", bid; "yes_ask_dollars", ask; "yes_bid_size_fp", bidSize; "yes_ask_size_fp", askSize] do
        set node field value
    importer.Event(root)
    let! count = importer.Refresh("mlb", 2026)
    Assert.Equal(30, count)
    let! rows = importer.Rows("mlb", 2026)
    let atl = rows |> Seq.find (fun r -> r.TeamAbbreviation = "ATL")
    Assert.Equal(expected, atl.PlayoffProbability.Value, 6)
    Assert.False(atl.PlayoffProbabilityIsFinal)
}

[<Theory>]
[<InlineData("ATL", "yes_bid_dollars", "0.9899")>]
[<InlineData("CIN", "yes_ask_dollars", "0.0101")>]
[<InlineData("ATL", "yes_ask_dollars", "0.9990")>]
[<InlineData("CIN", "yes_bid_dollars", "0.0010")>]
[<InlineData("ATL", "yes_bid_size_fp", "0.00")>]
[<InlineData("CIN", "yes_ask_size_fp", "0.00")>]
[<InlineData("ATL", "yes_ask_size_fp", "-1.00")>]
[<InlineData("CIN", "yes_bid_size_fp", "-1.00")>]
[<InlineData("ATL", "yes_ask_size_fp", null)>]
[<InlineData("CIN", "yes_bid_size_fp", null)>]
[<InlineData("ATL", "yes_bid_size_fp", "broken")>]
[<InlineData("CIN", "yes_ask_size_fp", "broken")>]
[<InlineData("ATL", "yes_ask_dollars", null)>]
[<InlineData("CIN", "yes_bid_dollars", null)>]
[<InlineData("ATL", "yes_bid_dollars", "1.01")>]
[<InlineData("CIN", "yes_ask_dollars", "-0.01")>]
[<InlineData("ATL", "status", "closed")>]
[<InlineData("CIN", "result", "no")>]
[<InlineData("ATL", "close_time", "2026-09-14T23:30:00Z")>]
[<InlineData("ATL", "market_type", "scalar")>]
[<InlineData("CIN", "settlement_value_dollars", "0.5000")>]
[<InlineData("ATL", "event_ticker", "KXMLBPLAYOFFS-25")>]
let ``Extreme quotes still reject weak missing malformed and mismatched evidence`` team field value = task {
    use importer = new Importer()
    let root = JsonNode.Parse(fixture (liveFixture "mlb"))
    importer.Event(root)
    let! _ = importer.Refresh("mlb", 2026)
    set (market root team) field value
    importer.Clock.Now <- scenarioTime.AddMinutes(5.0)
    let! _ = importer.Refresh("mlb", 2026)
    let! rows = importer.Rows("mlb", 2026)
    let row = rows |> Seq.find (fun r -> r.TeamAbbreviation = team)
    Assert.False(row.PlayoffProbability.HasValue)
    Assert.False(row.PlayoffProbabilityIsFinal)
    Assert.Equal(importer.Clock.Now.UtcDateTime, row.FetchedAt)
}

[<Theory>]
[<InlineData("yes_bid_dollars", "")>]
[<InlineData("yes_ask_dollars", "NaN")>]
[<InlineData("yes_bid_dollars", "Infinity")>]
[<InlineData("yes_bid_dollars", "-0.01")>]
[<InlineData("yes_ask_dollars", "1.01")>]
[<InlineData("yes_bid_dollars", "0.65")>]
[<InlineData("yes_ask_dollars", "0.8501")>]
[<InlineData("yes_bid_size_fp", "0.00")>]
[<InlineData("yes_ask_size_fp", "0.00")>]
[<InlineData("yes_ask_size_fp", "-1.00")>]
[<InlineData("yes_bid_size_fp", "broken")>]
[<InlineData("status", "closed")>]
[<InlineData("status", "inactive")>]
[<InlineData("status", "determined")>]
[<InlineData("status", "disputed")>]
[<InlineData("status", "amended")>]
[<InlineData("result", "yes")>]
[<InlineData("close_time", "2026-09-14T23:30:00Z")>]
[<InlineData("event_ticker", "KXNBAPLAYOFF-26")>]
[<InlineData("ticker", "KXNBAPLAYIN-27-BOS")>]
[<InlineData("market_type", "scalar")>]
[<InlineData("notional_value_dollars", "2.0000")>]
[<InlineData("settlement_value_dollars", "0.5000")>]
let ``Unusable NBA evidence immediately replaces an older estimate with unavailable`` field value = task {
    use importer = new Importer()
    let root = eventWithQuotes "nba"
    importer.Event(root)
    let! _ = importer.Refresh("nba", 2027)
    importer.Clock.Now <- scenarioTime.AddMinutes(5.0)
    set (market root "BOS") field value
    let! count = importer.Refresh("nba", 2027)
    Assert.Equal(29, count)
    let! rows = importer.Rows("nba", 2027)
    let row = rows |> Seq.find (fun r -> r.TeamAbbreviation = "BOS")
    Assert.False(row.PlayoffProbability.HasValue)
    Assert.False(row.PlayoffProbabilityIsFinal)
    Assert.Equal(importer.Clock.Now.UtcDateTime, row.FetchedAt)
}

[<Theory>]
[<InlineData("yes_bid_dollars")>]
[<InlineData("yes_ask_dollars")>]
[<InlineData("yes_bid_size_fp")>]
[<InlineData("yes_ask_size_fp")>]
[<InlineData("close_time")>]
let ``Missing quote fields never fall back to last trade`` (field: string) = task {
    use importer = new Importer()
    let root = eventWithQuotes "nba"
    let node = market root "BOS"
    node[field] <- null
    set node "last_price_dollars" "0.7500"
    importer.Event(root)
    let! count = importer.Refresh("nba", 2027)
    Assert.Equal(29, count)
    let! rows = importer.Rows("nba", 2027)
    Assert.False((rows |> Seq.find (fun r -> r.TeamAbbreviation = "BOS")).PlayoffProbability.HasValue)
}

[<Fact>]
let ``Twenty five point spread fractional depth and no trades are valid evidence`` () = task {
    use importer = new Importer()
    let root = eventWithQuotes "nba"
    let node = market root "BOS"
    set node "yes_ask_dollars" "0.8500"
    set node "yes_bid_size_fp" "0.01"
    set node "yes_ask_size_fp" "0.01"
    set node "volume_fp" "0.00"
    set node "volume_24h_fp" "0.00"
    set node "liquidity_dollars" "0.0000"
    importer.Event(root)
    let! count = importer.Refresh("nba", 2027)
    Assert.Equal(30, count)
    let! rows = importer.Rows("nba", 2027)
    let row = rows |> Seq.find (fun r -> r.TeamAbbreviation = "BOS")
    Assert.Equal(Nullable 0.725, row.PlayoffProbability)
    Assert.False(row.PlayoffProbabilityIsFinal)
}

[<Theory>]
[<InlineData("0.0000", 0.0)>]
[<InlineData("1.0000", 1.0)>]
let ``Endpoint prices are estimates not final outcomes`` price expected = task {
    use importer = new Importer()
    let root = eventWithQuotes "nba"
    let node = market root "BOS"
    set node "yes_bid_dollars" price
    set node "yes_ask_dollars" price
    importer.Event(root)
    let! _ = importer.Refresh("nba", 2027)
    let! rows = importer.Rows("nba", 2027)
    let row = rows |> Seq.find (fun r -> r.TeamAbbreviation = "BOS")
    Assert.Equal(Nullable expected, row.PlayoffProbability)
    Assert.False(row.PlayoffProbabilityIsFinal)
}

[<Theory>]
[<InlineData("finalized", "yes", "1.0000", 1.0)>]
[<InlineData("finalized", "no", "0.0000", 0.0)>]
[<InlineData("settled", "yes", "1.0000", 1.0)>]
let ``Confirmed binary settlement needs neither an open market nor quotes`` status result settlement expected = task {
    use importer = new Importer()
    let root = eventWithQuotes "nba"
    let node = market root "BOS"
    finalMarket node result settlement
    set node "status" status
    importer.Event(root)
    let! count = importer.Refresh("nba", 2027)
    Assert.Equal(30, count)
    let! rows = importer.Rows("nba", 2027)
    let row = rows |> Seq.find (fun r -> r.TeamAbbreviation = "BOS")
    Assert.Equal(Nullable expected, row.PlayoffProbability)
    Assert.True(row.PlayoffProbabilityIsFinal)
}

[<Theory>]
[<InlineData("yes", "0.5000")>]
[<InlineData("no", "0.5000")>]
[<InlineData("yes", "0.0000")>]
[<InlineData("no", "1.0000")>]
[<InlineData("scalar", "0.5000")>]
[<InlineData("", "1.0000")>]
[<InlineData("yes", "NaN")>]
[<InlineData("yes", "")>]
let ``Exceptional or inconsistent settlements never become qualification facts`` result settlement = task {
    use importer = new Importer()
    let root = eventWithQuotes "nba"
    finalMarket (market root "BOS") result settlement
    importer.Event(root)
    let! count = importer.Refresh("nba", 2027)
    Assert.Equal(29, count)
    let! rows = importer.Rows("nba", 2027)
    let row = rows |> Seq.find (fun r -> r.TeamAbbreviation = "BOS")
    Assert.False(row.PlayoffProbability.HasValue)
    Assert.False(row.PlayoffProbabilityIsFinal)
}

[<Theory>]
[<InlineData("nba", 2027, "sub_title", "2027-28")>]
[<InlineData("nba", 2027, "sub_title", "2025-26 or 2026-27")>]
[<InlineData("nba", 2027, "series_ticker", "KXNBAPLAYIN")>]
[<InlineData("nhl", 2027, "sub_title", "2027")>]
[<InlineData("nfl", 2026, "sub_title", "2025-26")>]
[<InlineData("mlb", 2026, "sub_title", "2026-27")>]
[<InlineData("mlb", 2026, "event_ticker", "KXMLBPLAYOFFS-25")>]
let ``Wrong or ambiguous season identity preserves old snapshot and timestamp`` sport season field value = task {
    use importer = new Importer()
    let root = eventWithQuotes sport
    importer.Event(root)
    let! originalCount = importer.Refresh(sport, season)
    importer.Clock.Now <- scenarioTime.AddMinutes(5.0)
    set (root["event"]) field value
    let! count = importer.Refresh(sport, season)
    Assert.Equal(0, count)
    let! rows = importer.Rows(sport, season)
    Assert.Equal(originalCount, rows.Count)
    Assert.All(rows, fun r ->
        Assert.Equal(Nullable 0.62, r.PlayoffProbability)
        Assert.Equal(scenarioTime.UtcDateTime, r.FetchedAt))
}

[<Theory>]
[<InlineData("{broken")>]
[<InlineData("null")>]
[<InlineData("{}")>]
[<InlineData("{\"event\":null}")>]
let ``Malformed events do not create a new snapshot`` body = task {
    use importer = new Importer()
    importer.Event(eventWithQuotes "nba")
    let! _ = importer.Refresh("nba", 2027)
    importer.Clock.Now <- scenarioTime.AddMinutes(5.0)
    importer.Respond <- fun _ _ -> Task.FromResult(jsonResponse HttpStatusCode.OK body)
    let! count = importer.Refresh("nba", 2027)
    Assert.Equal(0, count)
    let! rows = importer.Rows("nba", 2027)
    Assert.All(rows, fun r -> Assert.Equal(scenarioTime.UtcDateTime, r.FetchedAt))
}

[<Theory>]
[<InlineData(404)>]
[<InlineData(429)>]
[<InlineData(503)>]
let ``HTTP event failure leaves both estimates and finals untouched`` status = task {
    use importer = new Importer()
    let root = eventWithQuotes "nba"
    finalMarket (market root "BOS") "yes" "1.0000"
    importer.Event(root)
    let! _ = importer.Refresh("nba", 2027)
    importer.Clock.Now <- scenarioTime.AddMinutes(5.0)
    importer.Respond <- fun _ _ -> Task.FromResult(jsonResponse (enum<HttpStatusCode> status) "failure")
    let! count = importer.Refresh("nba", 2027)
    Assert.Equal(0, count)
    let! rows = importer.Rows("nba", 2027)
    Assert.All(rows, fun r -> Assert.Equal(scenarioTime.UtcDateTime, r.FetchedAt))
    Assert.True((rows |> Seq.find (fun r -> r.TeamAbbreviation = "BOS")).PlayoffProbabilityIsFinal)
    Assert.Equal(Nullable 0.62, (rows |> Seq.find (fun r -> r.TeamAbbreviation = "NY")).PlayoffProbability)
}

[<Fact>]
let ``Duplicate aliases invalidate that team and unknown teams are not imported`` () = task {
    use importer = new Importer()
    let root = eventWithQuotes "nba"
    let duplicate = (market root "GSW").DeepClone()
    set duplicate "ticker" "KXNBAPLAYOFF-27-GS"
    (root["event"]["markets"]).AsArray().Add(duplicate)
    let unknown = (market root "BOS").DeepClone()
    set unknown "ticker" "KXNBAPLAYOFF-27-UNKNOWN"
    (root["event"]["markets"]).AsArray().Add(unknown)
    (root["event"]["markets"]).AsArray().Add(null: JsonNode)
    importer.Event(root)
    let! count = importer.Refresh("nba", 2027)
    Assert.Equal(29, count)
    let! rows = importer.Rows("nba", 2027)
    Assert.Equal(30, rows.Count)
    Assert.False((rows |> Seq.find (fun r -> r.TeamAbbreviation = "GS")).PlayoffProbability.HasValue)
    Assert.DoesNotContain(rows, fun r -> r.TeamAbbreviation = "UNKNOWN")
}

[<Fact>]
let ``Missing nonfinal market becomes unavailable after an empty archive response`` () = task {
    use importer = new Importer()
    let root = eventWithQuotes "nba"
    importer.Event(root)
    let! _ = importer.Refresh("nba", 2027)
    removeMarket root "BOS"
    importer.Clock.Now <- scenarioTime.AddMinutes(5.0)
    let! count = importer.Refresh("nba", 2027)
    Assert.Equal(29, count)
    let! rows = importer.Rows("nba", 2027)
    let row = rows |> Seq.find (fun r -> r.TeamAbbreviation = "BOS")
    Assert.False(row.PlayoffProbability.HasValue)
    Assert.Equal(importer.Clock.Now.UtcDateTime, row.FetchedAt)
    let archive = importer.Requests |> Seq.find (fun uri -> uri.AbsolutePath.Contains("/historical/"))
    Assert.Equal("?event_ticker=KXNBAPLAYOFF-27&limit=1000", archive.Query)
}

[<Theory>]
[<InlineData(503, "failure")>]
[<InlineData(200, "{broken")>]
[<InlineData(200, "{\"markets\":[]}")>]
[<InlineData(200, "{\"markets\":null,\"cursor\":\"\"}")>]
let ``Archive failure clears missing estimates without advancing evidence age`` status body = task {
    use importer = new Importer()
    let root = eventWithQuotes "nba"
    importer.Event(root)
    let! _ = importer.Refresh("nba", 2027)
    removeMarket root "BOS"
    importer.Clock.Now <- scenarioTime.AddMinutes(5.0)
    withArchive importer root (enum<HttpStatusCode> status) body
    let! count = importer.Refresh("nba", 2027)
    Assert.Equal(29, count)
    let! rows = importer.Rows("nba", 2027)
    let missing = rows |> Seq.find (fun r -> r.TeamAbbreviation = "BOS")
    Assert.False(missing.PlayoffProbability.HasValue)
    Assert.False(missing.PlayoffProbabilityIsFinal)
    Assert.Equal(scenarioTime.UtcDateTime, missing.FetchedAt)
    Assert.Equal(importer.Clock.Now.UtcDateTime, (rows |> Seq.find (fun r -> r.TeamAbbreviation = "NY")).FetchedAt)
}

[<Fact>]
let ``Archive failure creates no evidence row for a never observed missing team`` () = task {
    use importer = new Importer()
    let root = eventWithQuotes "nba"
    removeMarket root "BOS"
    withArchive importer root HttpStatusCode.ServiceUnavailable "failure"
    let! count = importer.Refresh("nba", 2027)
    Assert.Equal(29, count)
    let! rows = importer.Rows("nba", 2027)
    Assert.Equal(29, rows.Count)
    Assert.DoesNotContain(rows, fun r -> r.TeamAbbreviation = "BOS")
}

[<Fact>]
let ``Known finals survive archive disappearance and failure with original timestamps`` () = task {
    use importer = new Importer()
    let root = eventWithQuotes "nba"
    finalMarket (market root "BOS") "yes" "1.0000"
    importer.Event(root)
    let! _ = importer.Refresh("nba", 2027)
    removeMarket root "BOS"
    removeMarket root "NYK"
    importer.Clock.Now <- scenarioTime.AddMonths(2)
    withArchive importer root HttpStatusCode.ServiceUnavailable "failure"
    let! count = importer.Refresh("nba", 2027)
    Assert.Equal(29, count)
    let! rows = importer.Rows("nba", 2027)
    let final = rows |> Seq.find (fun r -> r.TeamAbbreviation = "BOS")
    Assert.Equal(Nullable 1.0, final.PlayoffProbability)
    Assert.True(final.PlayoffProbabilityIsFinal)
    Assert.Equal(scenarioTime.UtcDateTime, final.FetchedAt)
    let missing = rows |> Seq.find (fun r -> r.TeamAbbreviation = "NY")
    Assert.False(missing.PlayoffProbability.HasValue)
    Assert.Equal(scenarioTime.UtcDateTime, missing.FetchedAt)
}

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``A newer invalidating database row prevents resurrection of an older final`` sameTimestamp = task {
    use importer = new Importer()
    let oldTime = scenarioTime.AddMinutes(-5.0).UtcDateTime
    let nextTime = if sameTimestamp then oldTime else scenarioTime.UtcDateTime
    do! importer.Seed(nbaRating "BOS" (Nullable 1.0) true oldTime)
    do! importer.Seed(nbaRating "BOS" (Nullable()) false nextTime)
    let root = eventWithQuotes "nba"
    removeMarket root "BOS"
    withArchive importer root HttpStatusCode.ServiceUnavailable "failure"
    let! _ = importer.Refresh("nba", 2027)
    let! rows = importer.Rows("nba", 2027)
    let boston = rows |> Seq.filter (fun r -> r.TeamAbbreviation = "BOS") |> Seq.toList
    Assert.Equal(2, boston.Length)
    Assert.All(boston, fun r ->
        Assert.False(r.PlayoffProbability.HasValue)
        Assert.False(r.PlayoffProbabilityIsFinal))
    Assert.Equal(nextTime, (boston |> List.maxBy (fun r -> r.Id)).FetchedAt)
}

[<Theory>]
[<InlineData("no", "0.0000", true)>]
[<InlineData("yes", "0.5000", false)>]
let ``Present corrected or fractional settlements supersede stored finals`` result settlement isFinal = task {
    use importer = new Importer()
    let root = eventWithQuotes "nba"
    finalMarket (market root "BOS") "yes" "1.0000"
    importer.Event(root)
    let! _ = importer.Refresh("nba", 2027)
    importer.Clock.Now <- scenarioTime.AddMinutes(5.0)
    finalMarket (market root "BOS") result settlement
    let! _ = importer.Refresh("nba", 2027)
    let! rows = importer.Rows("nba", 2027)
    let boston = rows |> Seq.find (fun r -> r.TeamAbbreviation = "BOS")
    Assert.Equal(isFinal, boston.PlayoffProbabilityIsFinal)
    Assert.Equal((if isFinal then Nullable 0.0 else Nullable()), boston.PlayoffProbability)
    Assert.Equal(importer.Clock.Now.UtcDateTime, boston.FetchedAt)
}

[<Fact>]
let ``Official archived NBA event recovers all final qualification outcomes`` () = task {
    use importer = new Importer()
    let root = JsonNode.Parse(fixture "kalshi-nba-playoffs-2026-event.json")
    withArchive importer root HttpStatusCode.OK (fixture "kalshi-nba-playoffs-2026-archived.json")
    let! count = importer.Refresh("nba", 2026)
    Assert.Equal(30, count)
    let! rows = importer.Rows("nba", 2026)
    Assert.Equal(16, rows |> Seq.filter (fun r -> r.PlayoffProbability = Nullable 1.0) |> Seq.length)
    Assert.Equal(14, rows |> Seq.filter (fun r -> r.PlayoffProbability = Nullable 0.0) |> Seq.length)
    Assert.All(rows, fun r ->
        Assert.True(r.PlayoffProbabilityIsFinal)
        Assert.Equal(Nullable 2026, r.SourceSeason))
    importer.Requests.Clear()
    importer.Clock.Now <- scenarioTime.AddDays(30.0)
    let! again = importer.Refresh("nba", 2026)
    Assert.Equal(30, again)
    Assert.DoesNotContain(importer.Requests, fun uri -> uri.AbsolutePath.Contains("/historical/"))
    let! retained = importer.Rows("nba", 2026)
    Assert.All(retained, fun r -> Assert.Equal(scenarioTime.UtcDateTime, r.FetchedAt))
}

[<Fact>]
let ``Archived finals are read to the terminal cursor and aliases stay league specific`` () = task {
    use importer = new Importer()
    let root = eventWithQuotes "nba"
    let boston = (market root "BOS").DeepClone()
    let goldenState = (market root "GSW").DeepClone()
    finalMarket boston "yes" "1.0000"
    finalMarket goldenState "no" "0.0000"
    removeMarket root "BOS"
    removeMarket root "GSW"
    importer.Respond <- fun request _ ->
        let body =
            if not (request.RequestUri.AbsolutePath.Contains("/historical/")) then root.ToJsonString()
            elif request.RequestUri.Query.Contains("cursor=") then archiveResponse [goldenState] ""
            else archiveResponse [boston] "page/+ =2"
        Task.FromResult(jsonResponse HttpStatusCode.OK body)
    let! count = importer.Refresh("nba", 2027)
    Assert.Equal(30, count)
    let! rows = importer.Rows("nba", 2027)
    Assert.Equal(Nullable 1.0, (rows |> Seq.find (fun r -> r.TeamAbbreviation = "BOS")).PlayoffProbability)
    Assert.Equal(Nullable 0.0, (rows |> Seq.find (fun r -> r.TeamAbbreviation = "GS")).PlayoffProbability)
    let second = importer.Requests |> Seq.filter (fun uri -> uri.AbsolutePath.Contains("/historical/")) |> Seq.last
    Assert.Contains("cursor=page%2F%2B%20%3D2", second.Query)
}

[<Theory>]
[<InlineData(true, 2)>]
[<InlineData(false, 3)>]
let ``Repeated or endless archive cursors cannot establish partial finals`` repeated expectedRequests = task {
    use importer = new Importer()
    do! importer.Seed(nbaRating "BOS" (Nullable 0.62) false scenarioTime.UtcDateTime)
    let root = eventWithQuotes "nba"
    let boston = (market root "BOS").DeepClone()
    finalMarket boston "yes" "1.0000"
    removeMarket root "BOS"
    let mutable page = 0
    importer.Respond <- fun request _ ->
        let body =
            if not (request.RequestUri.AbsolutePath.Contains("/historical/")) then root.ToJsonString()
            else
                page <- page + 1
                archiveResponse (if page = 1 then [boston] else []) (if repeated then "same" else string page)
        Task.FromResult(jsonResponse HttpStatusCode.OK body)
    importer.Clock.Now <- scenarioTime.AddMinutes(5.0)
    let! count = importer.Refresh("nba", 2027)
    Assert.Equal(29, count)
    Assert.Equal(expectedRequests, page)
    let! rows = importer.Rows("nba", 2027)
    let row = rows |> Seq.find (fun r -> r.TeamAbbreviation = "BOS")
    Assert.False(row.PlayoffProbability.HasValue)
    Assert.False(row.PlayoffProbabilityIsFinal)
    Assert.Equal(scenarioTime.UtcDateTime, row.FetchedAt)
}

[<Theory>]
[<InlineData("active")>]
[<InlineData("duplicate")>]
[<InlineData("fractional")>]
[<InlineData("wrong-event")>]
let ``Unconfirmed ambiguous or mismatched archive entries are never accepted`` scenario = task {
    use importer = new Importer()
    let root = eventWithQuotes "nba"
    let boston = (market root "BOS").DeepClone()
    if scenario <> "active" then finalMarket boston "yes" "1.0000"
    if scenario = "fractional" then set boston "settlement_value_dollars" "0.5000"
    if scenario = "wrong-event" then set boston "event_ticker" "KXNBAPLAYOFF-26"
    removeMarket root "BOS"
    let nodes = if scenario = "duplicate" then [boston; boston] else [boston]
    withArchive importer root HttpStatusCode.OK (archiveResponse nodes "")
    let! count = importer.Refresh("nba", 2027)
    Assert.Equal(29, count)
    let! rows = importer.Rows("nba", 2027)
    Assert.DoesNotContain(rows, fun r -> r.TeamAbbreviation = "BOS" && r.PlayoffProbability.HasValue)
}

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``Cancellation during event or archive fetch aborts the snapshot`` duringArchive = task {
    use importer = new Importer()
    use cancellation = new CancellationTokenSource()
    let root = eventWithQuotes "nba"
    if duringArchive then removeMarket root "BOS"
    importer.Respond <- fun request ct ->
        Assert.True(ct.CanBeCanceled)
        if not duringArchive || request.RequestUri.AbsolutePath.Contains("/historical/") then
            cancellation.Cancel()
            Task.FromCanceled<HttpResponseMessage>(ct)
        else Task.FromResult(jsonResponse HttpStatusCode.OK (root.ToJsonString()))
    let! _ = Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> importer.Service.RefreshAsync("nba", 2027, cancellation.Token) :> Task)
    let! rows = importer.Rows("nba", 2027)
    Assert.Empty(rows)
}

let private collegeEvent codes =
    let root = JsonNode.Parse("""{"event":{"event_ticker":"KXNCAAFPLAYOFF-26","series_ticker":"KXNCAAFPLAYOFF","sub_title":"2026-27","markets":[]}}""")
    for code in codes do
        let quote = JsonNode.Parse("""{"ticker":"","event_ticker":"KXNCAAFPLAYOFF-26","status":"active","result":"","yes_bid_dollars":"0.60","yes_ask_dollars":"0.64","yes_bid_size_fp":"10","yes_ask_size_fp":"10","close_time":"2026-12-29T15:00:00Z"}""")
        set quote "ticker" $"KXNCAAFPLAYOFF-26-{code}"
        (root["event"]["markets"]).AsArray().Add(quote)
    root

[<Fact>]
let ``College qualification maps Missouri South Carolina NC State Miami Ohio and Northwestern`` () = task {
    use importer = new Importer()
    do! importer.CollegeTeams ["MIZ"; "SC"; "NCSU"; "M-OH"; "NU"]
    importer.Event(collegeEvent ["MIZZ"; "SCAR"; "NCST"; "MOH"; "NW"])
    let! count = importer.Refresh("cfb", 2026)
    Assert.Equal(5, count)
    let! rows = importer.Rows("cfb", 2026)
    Assert.Equal(5, rows.Count)
    Assert.All(rows, fun row ->
        Assert.Equal(Nullable 0.62, row.PlayoffProbability)
        Assert.Equal("kalshi", row.Source)
        Assert.False(row.PlayoffProbabilityIsFinal))
}

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``Northwestern qualification alias preserves hype for unrelated unlisted college teams`` archived = task {
    use importer = new Importer()
    do! importer.CollegeTeams ["NU"; "OHIO"; "AKR"]
    let root = collegeEvent ["NW"]
    if archived then
        let northwestern = market root "NW"
        finalMarket northwestern "no" "0.0000"
        withArchive importer (collegeEvent []) HttpStatusCode.OK (archiveResponse [northwestern] "")
    else importer.Event(root)

    let now = importer.Clock.Now.UtcDateTime
    let game = Game(Id = "synthetic-cfb-alias", Sport = "cfb", HomeTeam = "Akron", AwayTeam = "Ohio",
                    HomeTeamAbbreviation = "AKR", AwayTeamAbbreviation = "OHIO", Status = "scheduled",
                    ScheduledStart = now.AddHours(1), SeasonYear = Nullable 2026, SeasonType = Nullable 2,
                    CollegeCompetition = "regular", HomeFpi = Nullable -10.0, AwayFpi = Nullable -4.0,
                    RatingsObservedAt = Nullable now)
    use scope = importer.Provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    db.Games.Add(game) |> ignore
    db.PregameOdds.Add(PregameOdds(GameId = game.Id, Source = "ESPN synthetic", FetchedAt = now,
                                 HomeMoneyline = Nullable 150, AwayMoneyline = Nullable -150,
                                 HomeWinProbability = 0.4, AwayWinProbability = 0.6)) |> ignore
    let! _ = db.SaveChangesAsync()
    let! _ = importer.Refresh("cfb", 2026)
    let hype = HypeScoreService(importer.Provider, NullLogger<HypeScoreService>.Instance, importer.Clock)
    let! score = hype.CalculateHypeScoreAsync(game)
    Assert.True(score.HasValue, "An unrelated Northwestern alias must not suppress the unlisted matchup's hype score")
    let! components = hype.GetHypeComponentsAsync(game)
    Assert.True(components.IsComplete)
    Assert.Equal(Nullable 0.005, components.HomePlayoffOdds)
    Assert.Equal(Nullable 0.005, components.AwayPlayoffOdds)

    let! rows = importer.Rows("cfb", 2026)
    Assert.Equal(3, rows.Count)
    for team in ["OHIO"; "AKR"] do
        let assumed = rows |> Seq.find (fun row -> row.TeamAbbreviation = team)
        Assert.Equal("cfp_no_market", assumed.Source)
        Assert.False(assumed.PlayoffProbabilityIsFinal)
        let! rank = GameDataService(db, importer.Clock).GetTeamRankInfoAsync("cfb", team, Nullable 2026, Nullable 2, CancellationToken.None)
        Assert.Equal(Some "CFP <1%", rank.Value.DisplayRank)
    let northwestern = rows |> Seq.find (fun row -> row.TeamAbbreviation = "NU")
    Assert.Equal("kalshi", northwestern.Source)
    Assert.Equal((if archived then Nullable 0.0 else Nullable 0.62), northwestern.PlayoffProbability)
    Assert.Equal(archived, northwestern.PlayoffProbabilityIsFinal)
}

[<Fact>]
let ``Unlisted college teams receive an explicitly sourced below one percent assumption`` () = task {
    use importer = new Importer()
    do! importer.CollegeTeams ["ORE"; "BOIS"]
    importer.Event(collegeEvent ["ORE"])
    let! count = importer.Refresh("cfb", 2026)
    Assert.Equal(1, count)  
    let! rows = importer.Rows("cfb", 2026)
    let assumed = rows |> Seq.find (fun row -> row.TeamAbbreviation = "BOIS")
    Assert.Equal(Nullable 0.005, assumed.PlayoffProbability)
    Assert.Equal("cfp_no_market", assumed.Source)
    Assert.Equal(Nullable 2026, assumed.SourceSeason)
    Assert.False(assumed.PlayoffProbabilityIsFinal)
    use scope = importer.Provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    let! rank = GameDataService(db, importer.Clock).GetTeamRankInfoAsync("cfb", "BOIS", Nullable 2026, Nullable 2, CancellationToken.None)
    Assert.Equal(Some "CFP <1%", rank.Value.DisplayRank)
    Assert.False(rank.Value.PlayoffIsFinal)
}

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``A new college market replaces the no-market assumption even when its quote is unusable`` usable = task {
    use importer = new Importer()
    do! importer.CollegeTeams ["ORE"; "BOIS"]
    importer.Event(collegeEvent ["ORE"])
    let! _ = importer.Refresh("cfb", 2026)
    importer.Clock.Now <- importer.Clock.Now.AddMinutes(5)
    let root = collegeEvent ["ORE"; "BSU"]
    if not usable then set (market root "BSU") "yes_bid_size_fp" "0"
    importer.Event(root)
    let! _ = importer.Refresh("cfb", 2026)
    let! rows = importer.Rows("cfb", 2026)
    Assert.Equal(2, rows.Count)
    let boise = rows |> Seq.find (fun row -> row.TeamAbbreviation = "BOIS")
    Assert.Equal("kalshi", boise.Source)
    Assert.Equal((if usable then Nullable 0.62 else Nullable()), boise.PlayoffProbability)
    let snapshot = PlayoffRatingPolicy.snapshot "cfb" 2026 importer.Clock.Now.UtcDateTime [boise]
    Assert.False(snapshot.Value.IsAssumed)
}

[<Theory>]
[<InlineData("yes_bid_size_fp", "0")>]
[<InlineData("yes_ask_size_fp", "0")>]
[<InlineData("yes_bid_dollars", "malformed")>]
[<InlineData("yes_ask_dollars", "0.99")>]
[<InlineData("status", "inactive")>]
let ``Listed college markets with unusable quotes never receive the no-market assumption`` field value = task {
    use importer = new Importer()
    do! importer.CollegeTeams ["BOIS"]
    let root = collegeEvent ["BSU"]
    set (market root "BSU") field value
    importer.Event(root)
    let! _ = importer.Refresh("cfb", 2026)
    let! rows = importer.Rows("cfb", 2026)
    let row = Assert.Single(rows)
    Assert.Equal("kalshi", row.Source)
    Assert.False(row.PlayoffProbability.HasValue)
}

[<Theory>]
[<InlineData("wrong-season")>]
[<InlineData("event-failure")>]
[<InlineData("archive-failure")>]
[<InlineData("incomplete-archive")>]
[<InlineData("unknown-live-team")>]
[<InlineData("unknown-archived-team")>]
[<InlineData("unverified-northwestern-live")>]
[<InlineData("unverified-northwestern-archive")>]
let ``College no-market assumptions require complete season-verified and mapped listings`` failure = task {
    use importer = new Importer()
    do! importer.CollegeTeams ["ORE"; "BOIS"]
    let root = collegeEvent ["ORE"]
    importer.Event(root)
    match failure with
    | "wrong-season" ->
        set root["event"] "sub_title" "2025-26"
        importer.Event(root)
    | "event-failure" -> importer.Respond <- fun _ _ -> Task.FromResult(jsonResponse HttpStatusCode.ServiceUnavailable "unavailable")
    | "archive-failure" -> withArchive importer root HttpStatusCode.ServiceUnavailable "unavailable"
    | "incomplete-archive" -> withArchive importer root HttpStatusCode.OK "{\"markets\":[]}"
    | "unknown-live-team" -> importer.Event(collegeEvent ["ORE"; "UNKNOWN"])
    | "unverified-northwestern-live" -> importer.Event(collegeEvent ["ORE"; "NW"])
    | _ ->
        let code = if failure = "unverified-northwestern-archive" then "NW" else "UNKNOWN"
        let archived = market (collegeEvent [code]) code
        finalMarket archived "no" "0.0000"
        withArchive importer root HttpStatusCode.OK (archiveResponse [archived] "")
    let! _ = importer.Refresh("cfb", 2026)
    let! rows = importer.Rows("cfb", 2026)
    Assert.DoesNotContain(rows, fun row -> row.Source = "cfp_no_market")
    Assert.DoesNotContain(rows, fun row -> row.TeamAbbreviation = "BOIS" && row.PlayoffProbability.HasValue)
}

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``Verified college final outcomes take precedence over absent live markets`` archived = task {
    use importer = new Importer()
    do! importer.CollegeTeams ["BOIS"]
    let root = collegeEvent []
    if archived then
        let settled = market (collegeEvent ["BSU"]) "BSU"
        finalMarket settled "yes" "1.0000"
        withArchive importer root HttpStatusCode.OK (archiveResponse [settled] "")
    else
        do! importer.Seed(TeamRating(Sport = "cfb", TeamAbbreviation = "BOIS", Source = "kalshi", Season = 2026,
                                    SourceSeason = Nullable 2026, PlayoffProbability = Nullable 1.0,
                                    PlayoffProbabilityIsFinal = true, FetchedAt = scenarioTime.UtcDateTime.AddDays(-1)))
        importer.Event(root)
    let! _ = importer.Refresh("cfb", 2026)
    let! rows = importer.Rows("cfb", 2026)
    let row = Assert.Single(rows)
    Assert.Equal("kalshi", row.Source)
    Assert.Equal(Nullable 1.0, row.PlayoffProbability)
    Assert.True(row.PlayoffProbabilityIsFinal)
}

[<Fact>]
let ``College no-market assumptions expire and failed requests do not renew them`` () = task {
    use importer = new Importer()
    do! importer.CollegeTeams ["BOIS"]
    importer.Event(collegeEvent [])
    let! _ = importer.Refresh("cfb", 2026)
    importer.Clock.Now <- importer.Clock.Now.AddHours(1)
    importer.Respond <- fun _ _ -> Task.FromResult(jsonResponse HttpStatusCode.ServiceUnavailable "unavailable")
    let! _ = importer.Refresh("cfb", 2026)
    let! rows = importer.Rows("cfb", 2026)
    let row = Assert.Single(rows)
    Assert.Equal(scenarioTime.UtcDateTime, row.FetchedAt)
    Assert.True((PlayoffRatingPolicy.playoffProbability "cfb" 2026 importer.Clock.Now.UtcDateTime rows).IsNone)
}

[<Fact>]
let ``College absence assumptions are not created for historical seasons or non-roster teams`` () = task {
    use importer = new Importer()
    do! importer.CollegeTeams ["BOIS"]
    importer.Clock.Now <- importer.Clock.Now.AddYears(1)
    importer.Event(collegeEvent [])
    let! _ = importer.Refresh("cfb", 2026)
    let! rows = importer.Rows("cfb", 2026)
    let row = Assert.Single(rows)
    Assert.Equal("BOIS", row.TeamAbbreviation)
    Assert.False(row.PlayoffProbability.HasValue)
    Assert.Equal("kalshi", row.Source)
}

[<Theory>]
[<InlineData("nfl")>]
[<InlineData("nba")>]
[<InlineData("nhl")>]
[<InlineData("mlb")>]
let ``The college no-market assumption source is never accepted for another sport`` sport =
    let row = TeamRating(Sport = sport, TeamAbbreviation = "TEST", Source = "cfp_no_market", Season = 2026,
                         SourceSeason = Nullable 2026, PlayoffProbability = Nullable 0.005, FetchedAt = scenarioTime.UtcDateTime)
    Assert.True((PlayoffRatingPolicy.playoffProbability sport 2026 scenarioTime.UtcDateTime [row]).IsNone)

[<Fact>]
let ``College qualification waits for a season-verified FBS roster`` () = task {
    use importer = new Importer()
    let! count = importer.Refresh("cfb", 2026)
    Assert.Equal(0, count)
    Assert.Empty(importer.Requests)
    let! rows = importer.Rows("cfb", 2026)
    Assert.Empty(rows)
}

[<Theory>]
[<InlineData("0.7500", "0.9700", true, 0.86)>]
[<InlineData("0.7000", "0.9500", true, 0.825)>]
[<InlineData("0.7000", "0.9501", false, 0.0)>]
let ``NHL qualification accepts Carolina quote and at most 25 cent spreads through stored selection`` bid ask accepted expected = task {
    use importer = new Importer()
    let root = eventWithQuotes "nhl"
    let node = market root "CAR"
    set node "yes_bid_dollars" bid
    set node "yes_ask_dollars" ask
    importer.Event(root)
    let! count = importer.Refresh("nhl", 2027)
    Assert.Equal((if accepted then 32 else 31), count)
    let! rows = importer.Rows("nhl", 2027)
    let carolina = rows |> Seq.find (fun r -> r.TeamAbbreviation = "CAR")
    let evidence = PlayoffQuoteEvidence.Parse(carolina.PlayoffEvidenceJson)
    Assert.Equal((if accepted then "estimate" else "unavailable"), evidence.State)
    Assert.Equal((if accepted then "accepted" else "wide_spread"), evidence.ReasonCode)
    Assert.False(carolina.PlayoffProbabilityIsFinal)
    let request : PlayoffRatingPolicy.SelectionRequest =
        { Sport = "nhl"; Season = 2027; Team = "CAR"; Now = scenarioTime.UtcDateTime; Options = PlayoffRatingsOptions() }
    let selected = PlayoffRatingPolicy.select request rows
    Assert.Equal((if accepted then Some expected else None), selected.Probability)
    Assert.Equal((if accepted then Some "kalshi" else None), selected.Source)
    use scope = importer.Provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    let! info = GameDataService(db, importer.Clock).GetTeamRankInfoAsync("nhl", "CAR", Nullable 2027, Nullable 2, CancellationToken.None)
    Assert.Equal(selected.Probability, info.Value.PlayoffOdds)
}

[<Fact>]
let ``NHL snapshots explain quote rejection without changing the probability policy`` () = task {
    use importer = new Importer()
    let root = eventWithQuotes "nhl"
    set (market root "BOS") "yes_bid_dollars" "0.4500"
    set (market root "BOS") "yes_ask_dollars" "0.9900"
    importer.Event(root)
    let! _ = importer.Refresh("nhl", 2027)
    let! rows = importer.Rows("nhl", 2027)
    let boston = rows |> Seq.find (fun r -> r.TeamAbbreviation = "BOS")
    Assert.False(boston.PlayoffProbability.HasValue)
    Assert.False(String.IsNullOrEmpty(boston.PlayoffEvidenceJson))
    let evidence = JsonNode.Parse(boston.PlayoffEvidenceJson)
    Assert.Equal("wide_spread", evidence["ReasonCode"].GetValue<string>())
    Assert.Equal("kalshi", evidence["Provider"].GetValue<string>())
    Assert.Equal("KXNHLPLAYOFF-27-BOS", evidence["MarketId"].GetValue<string>())
    Assert.Equal(0.45M, evidence["Bid"].GetValue<decimal>())
    Assert.Equal(0.99M, evidence["Ask"].GetValue<decimal>())
    Assert.True(isNull evidence["EvidenceAt"])
}

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``NHL final correction block survives quotes omission and outages until a verified correction`` fractional = task {
    use importer = new Importer()
    let root = eventWithQuotes "nhl"
    let node = market root "BOS"
    finalMarket node "yes" "1.0000"
    importer.Event(root)
    let! _ = importer.Refresh("nhl", 2027)
    importer.Clock.Now <- scenarioTime.AddMinutes(5)
    if fractional then finalMarket node "yes" "0.5000"
    else
        set node "status" "active"
        set node "result" ""
        node.AsObject().Remove("settlement_value_dollars") |> ignore
    let! _ = importer.Refresh("nhl", 2027)
    let! rows = importer.Rows("nhl", 2027)
    let row = rows |> Seq.find (fun r -> r.TeamAbbreviation = "BOS")
    let evidence = PlayoffQuoteEvidence.Parse(row.PlayoffEvidenceJson)
    Assert.True(evidence.ResolutionBlocked)
    Assert.Equal((if fractional then "exceptional_settlement" else "final_retracted"), evidence.ReasonCode)
    if fractional then
        Assert.Equal(Nullable 0.5M, evidence.YesPayout)
        Assert.Contains("finalized:yes", evidence.ResolutionReference)
    let blockedAt = evidence.ObservedAt
    Assert.False(row.PlayoffProbabilityIsFinal)
    let active = eventWithQuotes "nhl"
    importer.Event(active)
    importer.Clock.Now <- scenarioTime.AddMinutes(10)
    let! _ = importer.Refresh("nhl", 2027)
    let! activeRows = importer.Rows("nhl", 2027)
    let activeRow = activeRows |> Seq.find (fun r -> r.TeamAbbreviation = "BOS")
    Assert.Equal(Nullable 0.62, activeRow.PlayoffProbability)
    Assert.True((PlayoffQuoteEvidence.Parse(activeRow.PlayoffEvidenceJson)).ResolutionBlocked)
    removeMarket active "BOS"
    importer.Clock.Now <- scenarioTime.AddHours(2)
    withArchive importer active HttpStatusCode.ServiceUnavailable "failure"
    let! _ = importer.Refresh("nhl", 2027)
    let! missingRows = importer.Rows("nhl", 2027)
    let missing = missingRows |> Seq.find (fun r -> r.TeamAbbreviation = "BOS")
    Assert.Equal(blockedAt, (PlayoffQuoteEvidence.Parse(missing.PlayoffEvidenceJson)).ObservedAt)
    Assert.True((PlayoffQuoteEvidence.Parse(missing.PlayoffEvidenceJson)).ResolutionBlocked)
    let corrected = eventWithQuotes "nhl"
    finalMarket (market corrected "BOS") "no" "0.0000"
    importer.Event(corrected)
    let! _ = importer.Refresh("nhl", 2027)
    let! correctedRows = importer.Rows("nhl", 2027)
    let final = correctedRows |> Seq.find (fun r -> r.TeamAbbreviation = "BOS")
    Assert.Equal(Nullable 0.0, final.PlayoffProbability)
    Assert.True(final.PlayoffProbabilityIsFinal)
    Assert.False((PlayoffQuoteEvidence.Parse(final.PlayoffEvidenceJson)).ResolutionBlocked)
}

[<Fact>]
let ``NHL archive estimates are diagnosed as unavailable not freshly accepted quotes`` () = task {
    use importer = new Importer()
    let root = eventWithQuotes "nhl"
    let archived = (market root "BOS").DeepClone()
    removeMarket root "BOS"
    withArchive importer root HttpStatusCode.OK (archiveResponse [archived] "")
    let! _ = importer.Refresh("nhl", 2027)
    let! rows = importer.Rows("nhl", 2027)
    let boston = rows |> Seq.find (fun r -> r.TeamAbbreviation = "BOS")
    let e = PlayoffQuoteEvidence.Parse(boston.PlayoffEvidenceJson)
    Assert.False(boston.PlayoffProbability.HasValue)
    Assert.Equal("unavailable", e.State)
    Assert.Equal("awaiting_resolution", e.ReasonCode)
    Assert.False(e.EvidenceAt.HasValue)
}

[<Fact>]
let ``Changed NHL final contract semantics block selection rather than invent a new outcome`` () = task {
    use importer = new Importer()
    let root = eventWithQuotes "nhl"
    finalMarket (market root "BOS") "yes" "1.0000"
    importer.Event(root)
    let! _ = importer.Refresh("nhl", 2027)
    importer.Clock.Now <- scenarioTime.AddMinutes(5)
    set (market root "BOS") "market_type" "scalar"
    let! _ = importer.Refresh("nhl", 2027)
    let! rows = importer.Rows("nhl", 2027)
    let e = rows |> Seq.find (fun r -> r.TeamAbbreviation = "BOS") |> fun r -> PlayoffQuoteEvidence.Parse(r.PlayoffEvidenceJson)
    Assert.True(e.ResolutionBlocked)
    Assert.Equal("rules_changed", e.ReasonCode)
}

[<Fact>]
let ``NHL snapshot scalar and evidence changes roll back together on database failure`` () = task {
    use importer = new Importer()
    importer.Event(eventWithQuotes "nhl")
    let! _ = importer.Refresh("nhl", 2027)
    let! before = importer.Rows("nhl", 2027)
    use scope = importer.Provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    let! _ = db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_nhl_update BEFORE UPDATE ON team_ratings WHEN NEW.team_abbreviation = 'TOR' BEGIN SELECT RAISE(ABORT, 'simulated write failure'); END;")
    importer.Clock.Now <- scenarioTime.AddMinutes(5)
    let root = eventWithQuotes "nhl"
    finalMarket (market root "BOS") "yes" "1.0000"
    importer.Event(root)
    let! count = importer.Refresh("nhl", 2027)
    Assert.Equal(0, count)
    let! after = importer.Rows("nhl", 2027)
    for row in after do
        let original = before |> Seq.find (fun r -> r.TeamAbbreviation = row.TeamAbbreviation)
        Assert.Equal(original.PlayoffProbability, row.PlayoffProbability)
        Assert.Equal(original.FetchedAt, row.FetchedAt)
        Assert.Equal(original.PlayoffEvidenceJson, row.PlayoffEvidenceJson)
}

[<Fact>]
let ``Canceled NHL archive recovery writes neither new scalar nor diagnostic observations`` () = task {
    use importer = new Importer()
    let root = eventWithQuotes "nhl"
    importer.Event(root)
    let! _ = importer.Refresh("nhl", 2027)
    let! before = importer.Rows("nhl", 2027)
    importer.Clock.Now <- scenarioTime.AddMinutes(5)
    removeMarket root "BOS"
    use canceled = new CancellationTokenSource()
    importer.Respond <- fun request _ ->
        if request.RequestUri.AbsolutePath.Contains("/historical/") then
            canceled.Cancel()
            Task.FromCanceled<HttpResponseMessage>(canceled.Token)
        else Task.FromResult(jsonResponse HttpStatusCode.OK (root.ToJsonString()))
    let! _ = Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> importer.Service.RefreshAsync("nhl", 2027, canceled.Token))
    let! after = importer.Rows("nhl", 2027)
    for row in after do
        let original = before |> Seq.find (fun r -> r.TeamAbbreviation = row.TeamAbbreviation)
        Assert.Equal(original.FetchedAt, row.FetchedAt)
        Assert.Equal(original.PlayoffEvidenceJson, row.PlayoffEvidenceJson)
}

[<Fact>]
let ``NFL compatibility overload still requests only NFL`` () = task {
    use importer = new Importer()
    importer.Event(eventWithQuotes "nfl")
    let! count = importer.Service.RefreshAsync(2026, CancellationToken.None)
    Assert.Equal(32, count)
    Assert.Single(importer.Requests) |> ignore
    Assert.Equal("/trade-api/v2/events/KXNFLPLAYOFF-27", importer.Requests[0].AbsolutePath)
}

[<Theory>]
[<InlineData(9, "KXNFLPLAYOFF-27,KXNBAPLAYOFF-26,KXNBAPLAYOFF-27,KXNHLPLAYOFF-26,KXNHLPLAYOFF-27,KXMLBPLAYOFFS-26")>]
[<InlineData(11, "KXNFLPLAYOFF-27,KXNBAPLAYOFF-27,KXNHLPLAYOFF-27,KXMLBPLAYOFFS-26,KXMLBPLAYOFFS-27")>]
[<InlineData(1, "KXNFLPLAYOFF-26,KXNBAPLAYOFF-26,KXNHLPLAYOFF-26,KXMLBPLAYOFFS-26")>]
let ``Refresh cycle visits only current and policy upcoming seasons despite failures`` month expected = task {
    use importer = new Importer()
    importer.Clock.Now <- DateTimeOffset(2026, month, 14, 12, 0, 0, TimeSpan.Zero)
    importer.Respond <- fun _ _ -> Task.FromResult(jsonResponse HttpStatusCode.NotFound "missing")
    let! count = importer.Service.RefreshCurrentAsync(CancellationToken.None)
    Assert.Equal(0, count)
    let actual = importer.Requests |> Seq.map (fun uri -> uri.Segments |> Array.last) |> String.concat ","
    Assert.Equal(expected, actual)
}
