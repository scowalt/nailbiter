module NflLowInterestDisplayTests

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Falco.Markup
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

let private lowInterest root team =
    let node = market root team
    for field, value in [ "yes_bid_dollars", "0.0000"; "yes_ask_dollars", "0.0200"; "yes_bid_size_fp", "0.00"; "yes_ask_size_fp", "1.25"; "volume_fp", "0.00"; "volume_24h_fp", "0.00" ] do
        node[field] <- JsonValue.Create(value)
    node

let private game () =
    Game(Id = "synthetic-nfl-low-interest", Sport = "nfl", Status = "scheduled", SeasonYear = Nullable 2026,
         SeasonType = Nullable 2, WeekNumber = Nullable 1, ScheduledStart = now.AddHours(4.0).UtcDateTime,
         AwayTeam = "Synthetic Buffalo", AwayTeamAbbreviation = "BUF", HomeTeam = "Synthetic Kansas City", HomeTeamAbbreviation = "KC")

[<Fact>]
let ``Low interest display is compact without promoting a probability or final outcome`` () = task {
    use h = new Harness()
    let root = payload ()
    lowInterest root "KC" |> ignore
    let! count = h.Refresh(root)
    Assert.Equal(31, count)
    let! rank = h.Rank("KC")
    Assert.Equal(Some "<1% playoff odds", rank.Value.DisplayRank)
    Assert.True(rank.Value.PlayoffOdds.IsNone)
    Assert.False(rank.Value.PlayoffIsFinal)
    Assert.False(rank.Value.PlayoffIsAssumed)
    Assert.Equal(backend.Scoring.ProQualificationEvidenceKind.Unavailable, rank.Value.PlayoffEvidenceKind)
    use scope = h.Provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    let! row = db.TeamRatings.SingleAsync(fun r -> r.TeamAbbreviation = "KC")
    Assert.False(row.PlayoffProbability.HasValue)
    Assert.False(row.PlayoffProbabilityIsFinal)
    let evidence = PlayoffQuoteEvidence.Parse(row.PlayoffEvidenceJson)
    Assert.Equal("insufficient_depth", evidence.ReasonCode)
    Assert.Equal(Nullable 0.02M, evidence.Ask)
    Assert.Single(h.Requests) |> ignore
}

[<Theory>]
[<InlineData("home")>]
[<InlineData("league")>]
[<InlineData("detail")>]
let ``Every page uses the owner selected compact display`` page = task {
    use h = new Harness()
    let root = payload ()
    lowInterest root "KC" |> ignore
    lowInterest root "BUF" |> ignore
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
    Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "&lt;1%").Count)
    Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "aria-description=\"Low-interest display convention, not an exact probability.\"").Count)
    Assert.DoesNotContain("0–2%", html)
    Assert.DoesNotContain("Missed playoffs", html)
    Assert.DoesNotContain("Clinched playoffs", html)
    let directory = Environment.GetEnvironmentVariable("NFL_LOW_INTEREST_ARTIFACT_DIR")
    if not (String.IsNullOrWhiteSpace(directory)) then
        Directory.CreateDirectory(directory) |> ignore
        File.WriteAllText(Path.Combine(directory, page + ".html"), "<!doctype html>" + html)
}

[<Theory>]
[<InlineData("yes_ask_dollars", "0.0201")>]
[<InlineData("yes_ask_dollars", "0.05")>]
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
let ``Missing unsafe and less extreme quotes are never assigned the display`` (field: string) (value: string) = task {
    use h = new Harness()
    let root = payload ()
    let node = lowInterest root "KC"
    node[field] <- (if isNull value then null else JsonValue.Create(value))
    let! _ = h.Refresh(root)
    let! rank = h.Rank("KC")
    Assert.Equal(Some "Playoff odds unavailable", rank.Value.DisplayRank)
    Assert.True(rank.Value.PlayoffOdds.IsNone)
}

[<Theory>]
[<InlineData(3599.0, true)>]
[<InlineData(3600.0, false)>]
[<InlineData(-1.0, false)>]
let ``Display observation expires without becoming a probability`` seconds expected = task {
    use h = new Harness()
    let root = payload ()
    lowInterest root "KC" |> ignore
    let! _ = h.Refresh(root)
    h.Clock.Now <- now.AddSeconds(seconds)
    let! rank = h.Rank("KC")
    Assert.Equal(Some(if expected then "<1% playoff odds" else "Playoff odds unavailable"), rank.Value.DisplayRank)
    Assert.True(rank.Value.PlayoffOdds.IsNone)
}

[<Fact>]
let ``A later usable quote removes the display convention`` () = task {
    use h = new Harness()
    let root = payload ()
    let node = lowInterest root "KC"
    let! _ = h.Refresh(root)
    node["yes_bid_dollars"] <- JsonValue.Create("0.60")
    node["yes_ask_dollars"] <- JsonValue.Create("0.64")
    node["yes_bid_size_fp"] <- JsonValue.Create("2.5")
    h.Clock.Now <- now.AddMinutes(5.0)
    let! _ = h.Refresh(root)
    let! rank = h.Rank("KC")
    Assert.Equal(Some 0.62, rank.Value.PlayoffOdds)
    Assert.DoesNotContain("<1%", rank.Value.DisplayRank.Value)
    Assert.True(rank.Value.PlayoffDisplayOverride.IsNone)
}

[<Theory>]
[<InlineData("Version", "0")>]
[<InlineData("ValidationPolicy", "\"unverified\"")>]
[<InlineData("Provider", "\"other\"")>]
[<InlineData("Sport", "\"nba\"")>]
[<InlineData("Team", "\"BUF\"")>]
[<InlineData("Season", "2025")>]
[<InlineData("EventId", "\"KXNFLPLAYOFF-26\"")>]
[<InlineData("MarketId", "\"KXNFLPLAYOFF-27-BUF\"")>]
[<InlineData("ContractVerified", "false")>]
[<InlineData("State", "\"estimate\"")>]
[<InlineData("State", "\"final\"")>]
[<InlineData("ReasonCode", "\"awaiting_resolution\"")>]
[<InlineData("ResolutionBlocked", "true")>]
[<InlineData("ObservedAt", "null")>]
[<InlineData("ObservedAt", "\"2026-09-13T18:00:01Z\"")>]
[<InlineData("RefreshId", "\"\"")>]
[<InlineData("BidExplicitlyEmpty", "false")>]
[<InlineData("AskExplicitlyEmpty", "true")>]
let ``Presentation requires the complete stored observation identity`` (field: string) (value: string) = task {
    use h = new Harness()
    let root = payload ()
    lowInterest root "KC" |> ignore
    let! _ = h.Refresh(root)
    use scope = h.Provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    let! row = db.TeamRatings.SingleAsync(fun r -> r.TeamAbbreviation = "KC")
    let json = JsonNode.Parse(row.PlayoffEvidenceJson)
    json[field] <- JsonNode.Parse(value)
    row.PlayoffEvidenceJson <- json.ToJsonString()
    let! _ = db.SaveChangesAsync()
    let! rank = h.Rank("KC")
    Assert.True(rank.Value.PlayoffDisplayOverride.IsNone)
    Assert.True(rank.Value.PlayoffOdds.IsNone)
}

[<Theory>]
[<InlineData(0)>]
[<InlineData(1)>]
[<InlineData(3)>]
let ``The display does not reclassify preseason unknown or postseason competition`` phase = task {
    use h = new Harness()
    let root = payload ()
    lowInterest root "KC" |> ignore
    let! _ = h.Refresh(root)
    use scope = h.Provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    let! rank = GameDataService(db, h.Clock).GetTeamRankInfoAsync("nfl", "KC", Nullable 2026, Nullable phase, CancellationToken.None)
    Assert.True(rank.Value.PlayoffDisplayOverride.IsNone)
}

[<Fact>]
let ``A newer observation without usable quotes cannot reuse an older display`` () = task {
    use h = new Harness()
    let root = payload ()
    lowInterest root "KC" |> ignore
    let! _ = h.Refresh(root)
    use scope = h.Provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    h.Clock.Now <- now.AddMinutes(5.0)
    db.TeamRatings.Add(TeamRating(Sport = "nfl", TeamAbbreviation = "KC", Source = "kalshi", Season = 2026, SourceSeason = Nullable 2026,
                                 FetchedAt = h.Clock.Now.UtcDateTime)) |> ignore
    let! _ = db.SaveChangesAsync()
    let! rank = h.Rank("KC")
    Assert.Equal(Some "Playoff odds unavailable", rank.Value.DisplayRank)
    Assert.True(rank.Value.PlayoffDisplayOverride.IsNone)
}

[<Theory>]
[<InlineData("nba")>]
[<InlineData("nhl")>]
[<InlineData("mlb")>]
[<InlineData("cfb")>]
let ``The display convention does not activate another sport`` sport = task {
    use h = new Harness()
    let root = payload ()
    lowInterest root "KC" |> ignore
    let! _ = h.Refresh(root)
    use scope = h.Provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    let! row = db.TeamRatings.SingleAsync(fun r -> r.TeamAbbreviation = "KC")
    row.Sport <- sport
    let! _ = db.SaveChangesAsync()
    let! rank = GameDataService(db, h.Clock).GetTeamRankInfoAsync(sport, "KC", Nullable 2026, Nullable 2, CancellationToken.None)
    Assert.True(rank.Value.PlayoffDisplayOverride.IsNone)
    Assert.True(rank.Value.PlayoffOdds.IsNone)
}

[<Fact>]
let ``A populated low priced bid keeps the real selected estimate`` () = task {
    use h = new Harness()
    let root = payload ()
    let node = lowInterest root "KC"
    node["yes_bid_dollars"] <- JsonValue.Create("0.01")
    node["yes_bid_size_fp"] <- JsonValue.Create("0.5")
    let! _ = h.Refresh(root)
    let! rank = h.Rank("KC")
    Assert.Equal(Some 0.015, rank.Value.PlayoffOdds)
    Assert.True(rank.Value.PlayoffDisplayOverride.IsNone)
}

[<Fact>]
let ``Canonical NFL aliases keep the same display identity`` () = task {
    use h = new Harness()
    let root = payload ()
    lowInterest root "JAC" |> ignore
    let! _ = h.Refresh(root)
    let! rank = h.Rank("JAX")
    Assert.Equal(Some "<1%", rank.Value.PlayoffDisplayOverride)
    Assert.True(rank.Value.PlayoffOdds.IsNone)
}

[<Fact>]
let ``A new wider one sided quote withdraws the old display`` () = task {
    use h = new Harness()
    let root = payload ()
    let node = lowInterest root "KC"
    let! _ = h.Refresh(root)
    let! before = h.Rank("KC")
    Assert.Equal(Some "<1%", before.Value.PlayoffDisplayOverride)
    node["yes_ask_dollars"] <- JsonValue.Create("0.05")
    h.Clock.Now <- now.AddMinutes(5.0)
    let! _ = h.Refresh(root)
    let! after = h.Rank("KC")
    Assert.True(after.Value.PlayoffDisplayOverride.IsNone)
    Assert.True(after.Value.PlayoffOdds.IsNone)
    Assert.Equal(Some "Playoff odds unavailable", after.Value.DisplayRank)
}

[<Fact>]
let ``Legacy null probability without retained quotes does not imply low interest`` () = task {
    use h = new Harness()
    let root = payload ()
    lowInterest root "KC" |> ignore
    let! _ = h.Refresh(root)
    use scope = h.Provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    let! row = db.TeamRatings.SingleAsync(fun r -> r.TeamAbbreviation = "KC")
    row.PlayoffEvidenceJson <- null
    let! _ = db.SaveChangesAsync()
    let! rank = h.Rank("KC")
    Assert.True(rank.Value.PlayoffDisplayOverride.IsNone)
    Assert.Equal(Some "Playoff odds unavailable", rank.Value.DisplayRank)
}
