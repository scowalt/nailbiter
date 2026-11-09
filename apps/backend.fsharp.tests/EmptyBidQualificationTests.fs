module EmptyBidQualificationTests

open System
open System.Collections.Generic
open System.IO
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
open backend.GraphQL.Types
open backend.Models
open backend.Services
open backend.Services.Kalshi
open Backend.Rendering

let private now = DateTimeOffset(2026, 9, 13, 18, 0, 0, TimeSpan.Zero)

type private Clock() =
    inherit TimeProvider()
    member val Now = now with get, set
    override this.GetUtcNow() = this.Now

type private Harness() =
    let connection = new SqliteConnection("Data Source=:memory:")
    do connection.Open()
    let services = ServiceCollection()
    do services.AddDbContext<ExcitementDbContext>(fun options -> options.UseSqlite(connection) |> ignore) |> ignore
    let provider = services.BuildServiceProvider()
    let clock = Clock()
    let mutable json = ""
    let requests = ResizeArray<string>()
    let handler =
        { new HttpMessageHandler() with
            override _.SendAsync(request, _) =
                requests.Add(request.RequestUri.PathAndQuery)
                let response = new HttpResponseMessage(HttpStatusCode.OK)
                response.Content <- new StringContent(json)
                Task.FromResult(response) }
    let http = new HttpClient(handler)
    let importer = KalshiPlayoffRatingsService(http, NullLogger<KalshiPlayoffRatingsService>.Instance, provider, clock)
    do
        use scope = provider.CreateScope()
        scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.EnsureCreated() |> ignore
    member _.Clock = clock
    member _.Provider = provider
    member _.Requests = requests
    member _.Refresh(root: JsonNode) =
        json <- root.ToJsonString()
        importer.RefreshAsync("nfl", 2026, CancellationToken.None)
    member _.Rank(team) = task {
        use scope = provider.CreateScope()
        let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
        return! GameDataService(db, clock).GetTeamRankInfoAsync("nfl", team, Nullable 2026, Nullable 2, CancellationToken.None)
    }
    interface IDisposable with
        member _.Dispose() = http.Dispose(); handler.Dispose(); provider.Dispose(); connection.Dispose()

let private payload () =
    JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "kalshi-nfl-playoffs-2026.json")))

let private market (root: JsonNode) team =
    (root["event"]["markets"]).AsArray() |> Seq.find (fun m -> (m["ticker"]).GetValue<string>() = "KXNFLPLAYOFF-27-" + team)

let private noBuyers root team =
    let node = market root team
    for field, value in [ "yes_bid_dollars", "0.0000"; "yes_ask_dollars", "0.0400"; "yes_bid_size_fp", "0.00"; "yes_ask_size_fp", "17.63" ] do
        node[field] <- JsonValue.Create(value)
    node

let private game () =
    Game(Id = "synthetic-nfl-no-buyers", Sport = "nfl", Status = "scheduled", SeasonYear = Nullable 2026,
         SeasonType = Nullable 2, WeekNumber = Nullable 1, ScheduledStart = now.AddHours(4.0).UtcDateTime,
         AwayTeam = "Synthetic Buffalo", AwayTeamAbbreviation = "BUF", HomeTeam = "Synthetic Kansas City", HomeTeamAbbreviation = "KC")

[<Fact>]
let ``A market with no buyers shows its ask as an upper bound and scores the midpoint`` () = task {
    use h = new Harness()
    let root = payload ()
    noBuyers root "KC" |> ignore
    let! count = h.Refresh(root)
    Assert.Equal(32, count)
    let! rank = h.Rank("KC")
    Assert.Equal(Some "<4% playoff odds", rank.Value.DisplayRank)
    Assert.Equal(Some 0.02, rank.Value.PlayoffOdds)
    Assert.Equal(Some 0.04, rank.Value.PlayoffUpperBound)
    Assert.False(rank.Value.PlayoffIsFinal)
    Assert.Equal(backend.Scoring.ProQualificationEvidenceKind.Kalshi, rank.Value.PlayoffEvidenceKind)
    use scope = h.Provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    let! row = db.TeamRatings.SingleAsync(fun r -> r.TeamAbbreviation = "KC")
    Assert.Equal(Nullable 0.02, row.PlayoffProbability)
    let evidence = PlayoffQuoteEvidence.Parse(row.PlayoffEvidenceJson)
    Assert.Equal("estimate", evidence.State)
    Assert.Equal(Nullable 0.04M, evidence.Ask)
    Assert.Single(h.Requests) |> ignore
}

[<Theory>]
[<InlineData("home")>]
[<InlineData("league")>]
[<InlineData("detail")>]
let ``Every page shows the upper bound`` page = task {
    use h = new Harness()
    let root = payload ()
    noBuyers root "KC" |> ignore
    noBuyers root "BUF" |> ignore
    let! _ = h.Refresh(root)
    use scope = h.Provider.CreateScope()
    let data = GameDataService(scope.ServiceProvider.GetRequiredService<ExcitementDbContext>(), h.Clock)
    let! ranks = data.GetTeamRankInfosAsync("nfl", ["KC"; "BUF"], Nullable 2026, Nullable 2, CancellationToken.None)
    let keyed = Dictionary<string, TeamRankInfo>()
    for entry in ranks do keyed[$"nfl:2026:2:{entry.Key}"] <- entry.Value
    let games = List<Game>([game ()])
    let odds = Dictionary<string, OddsSnapshot>()
    let scores = Dictionary<string, Nullable<float>>()
    let content =
        match page with
        | "home" -> HomePageRenderer.Render games odds scores scores keyed
        | "league" -> LeaguePageRenderer.Render Sport.NFL (LeagueConfig.getLeagueConfig Sport.NFL) games odds scores scores None keyed None None None
        | _ -> GameDetailRenderer.RenderAtTime now.UtcDateTime games[0] (List<OddsSnapshot>()) None Unchecked.defaultof<HypeComponents> (Nullable()) (Some ranks["KC"]) (Some ranks["BUF"])
    let html = Layout.renderPage "Synthetic NFL display" "Offline display verification" None content
    Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "&lt;4%").Count)
    Assert.DoesNotContain("Playoff odds unavailable", html)
}

[<Theory>]
[<InlineData("0.0010", 0.0005, "<1%")>]
[<InlineData("0.0100", 0.005, "<1%")>]
[<InlineData("0.0200", 0.01, "<2%")>]
[<InlineData("0.0450", 0.0225, "<5%")>]
[<InlineData("0.0500", 0.025, "<5%")>]
let ``Asks up to five cents round the bound up to a whole percent`` (ask: string) (expected: float) (display: string) = task {
    use h = new Harness()
    let root = payload ()
    (noBuyers root "KC")["yes_ask_dollars"] <- JsonValue.Create(ask)
    let! _ = h.Refresh(root)
    let! rank = h.Rank("KC")
    Assert.Equal(expected, rank.Value.PlayoffOdds.Value, 6)
    Assert.Equal(Some(display + " playoff odds"), rank.Value.DisplayRank)
}

[<Theory>]
[<InlineData("yes_ask_dollars", "0.0501")>]
[<InlineData("yes_ask_dollars", "0.06")>]
[<InlineData("yes_ask_dollars", "broken")>]
[<InlineData("yes_ask_dollars", null)>]
[<InlineData("yes_ask_size_fp", "0")>]
[<InlineData("yes_ask_size_fp", "-1")>]
[<InlineData("yes_bid_dollars", "0.001")>]
[<InlineData("yes_bid_dollars", "-0.01")>]
[<InlineData("yes_bid_size_fp", null)>]
[<InlineData("yes_bid_size_fp", "broken")>]
[<InlineData("status", "inactive")>]
[<InlineData("close_time", "2026-09-13T17:59:59Z")>]
[<InlineData("event_ticker", "KXNFLPLAYOFF-26")>]
[<InlineData("market_type", "scalar")>]
[<InlineData("notional_value_dollars", "2.0000")>]
[<InlineData("settlement_value_dollars", "0.5")>]
let ``Wider missing and unsafe quotes stay unavailable`` (field: string) (value: string) = task {
    use h = new Harness()
    let root = payload ()
    let node = noBuyers root "KC"
    node[field] <- (if isNull value then null else JsonValue.Create(value))
    let! _ = h.Refresh(root)
    let! rank = h.Rank("KC")
    Assert.Equal(Some "Playoff odds unavailable", rank.Value.DisplayRank)
    Assert.True(rank.Value.PlayoffOdds.IsNone)
    Assert.True(rank.Value.PlayoffUpperBound.IsNone)
}

[<Fact>]
let ``A no-buyer ask above the cap keeps its insufficient depth reason`` () = task {
    use h = new Harness()
    let root = payload ()
    (noBuyers root "KC")["yes_ask_dollars"] <- JsonValue.Create("0.0600")
    let! _ = h.Refresh(root)
    use scope = h.Provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    let! row = db.TeamRatings.SingleAsync(fun r -> r.TeamAbbreviation = "KC")
    Assert.False(row.PlayoffProbability.HasValue)
    let evidence = PlayoffQuoteEvidence.Parse(row.PlayoffEvidenceJson)
    Assert.Equal("insufficient_depth", evidence.ReasonCode)
    Assert.Equal(Nullable 0.06M, evidence.Ask)
}

[<Theory>]
[<InlineData(3599.0, true)>]
[<InlineData(3600.0, false)>]
let ``The bound expires with the quote`` seconds expected = task {
    use h = new Harness()
    let root = payload ()
    noBuyers root "KC" |> ignore
    let! _ = h.Refresh(root)
    h.Clock.Now <- now.AddSeconds(seconds)
    let! rank = h.Rank("KC")
    Assert.Equal(Some(if expected then "<4% playoff odds" else "Playoff odds unavailable"), rank.Value.DisplayRank)
}

[<Fact>]
let ``A later two sided quote replaces the bound`` () = task {
    use h = new Harness()
    let root = payload ()
    let node = noBuyers root "KC"
    let! _ = h.Refresh(root)
    node["yes_bid_dollars"] <- JsonValue.Create("0.02")
    node["yes_bid_size_fp"] <- JsonValue.Create("2.5")
    h.Clock.Now <- now.AddMinutes(5.0)
    let! _ = h.Refresh(root)
    let! rank = h.Rank("KC")
    Assert.Equal(0.03, rank.Value.PlayoffOdds.Value, 6)
    Assert.True(rank.Value.PlayoffUpperBound.IsNone)
    Assert.DoesNotContain("<", rank.Value.DisplayRank.Value)
    use scope = h.Provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    let! row = db.TeamRatings.SingleAsync(fun r -> r.TeamAbbreviation = "KC")
    Assert.Null(row.PlayoffEvidenceJson)
}

[<Theory>]
[<InlineData("Ask", "0.03")>]
[<InlineData("BidExplicitlyEmpty", "false")>]
[<InlineData("State", "\"unavailable\"")>]
[<InlineData("Team", "\"BUF\"")>]
[<InlineData("Season", "2025")>]
[<InlineData("ContractVerified", "false")>]
[<InlineData("ObservedAt", "\"2026-09-13T18:00:01Z\"")>]
let ``Stored evidence that disagrees with the probability is rejected`` (field: string) (value: string) = task {
    use h = new Harness()
    let root = payload ()
    noBuyers root "KC" |> ignore
    let! _ = h.Refresh(root)
    use scope = h.Provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    let! row = db.TeamRatings.SingleAsync(fun r -> r.TeamAbbreviation = "KC")
    let json = JsonNode.Parse(row.PlayoffEvidenceJson)
    json[field] <- JsonNode.Parse(value)
    row.PlayoffEvidenceJson <- json.ToJsonString()
    let! _ = db.SaveChangesAsync()
    let! rank = h.Rank("KC")
    Assert.True(rank.Value.PlayoffOdds.IsNone)
    Assert.True(rank.Value.PlayoffUpperBound.IsNone)
}

[<Fact>]
let ``Canonical NFL aliases keep the bound`` () = task {
    use h = new Harness()
    let root = payload ()
    noBuyers root "JAC" |> ignore
    let! _ = h.Refresh(root)
    let! rank = h.Rank("JAX")
    Assert.Equal(Some 0.04, rank.Value.PlayoffUpperBound)
    Assert.Equal(Some "<4% playoff odds", rank.Value.DisplayRank)
}
