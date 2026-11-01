module NflPlayoffTests

open System
open System.IO
open System.Linq
open System.Text.Json.Nodes
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Microsoft.EntityFrameworkCore
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open backend.Data
open backend.Models
open backend.Services
open backend.Services.PowerRankingsGuru
open backend.Services.Kalshi
open Backend.Rendering
open Falco.Markup

let private now = DateTimeOffset(2026, 9, 13, 18, 0, 0, TimeSpan.Zero)

type private Clock() =
    inherit TimeProvider()
    member val Now = now with get, set
    override this.GetUtcNow() = this.Now

let private createDatabase () =
    let connection = new SqliteConnection("Data Source=:memory:")
    connection.Open()
    let services = ServiceCollection()
    services.AddDbContext<ExcitementDbContext>(fun options -> options.UseSqlite(connection) |> ignore) |> ignore
    let provider = services.BuildServiceProvider()
    use scope = provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    db.Database.EnsureCreated() |> ignore
    connection, provider

let private rankingsHtml season =
    $"""<h1 class="content-title">{season} Composite NFL Power Rankings</h1>
    <table class="rankings-table"><tbody><tr>
    <td class="rank-v2">18</td>
    <td><span class="full-name">Chiefs</span><span class="abbrv">KC</span></td>
    <td class="playoff-odds">&lt;1%%</td><td class="champ-odds">&lt;1%%</td>
    </tr></tbody></table>"""

[<Fact>]
let ``NFL scraper rejects previous season instead of relabeling it`` () = task {
    let season = 2026
    use handler =
        { new HttpMessageHandler() with
            override _.SendAsync(_, _) =
                let response = new HttpResponseMessage(HttpStatusCode.OK)
                response.Content <- new StringContent(rankingsHtml (season - 1))
                Task.FromResult(response) }
    use http = new HttpClient(handler)
    let connection, provider = createDatabase ()
    use _connection = connection
    use _provider = provider
    let service = PowerRankingsGuruService(http, NullLogger<PowerRankingsGuruService>.Instance, provider, Clock())
    let! count = service.RefreshNflRatingsAsync()
    Assert.Equal(0, count)
    use scope = provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    Assert.Empty(db.TeamRatings)
}

[<Fact>]
let ``NFL display never uses Power Rankings Guru playoff probabilities`` () = task {
    let connection, provider = createDatabase ()
    use _connection = connection
    use _provider = provider
    use scope = provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    db.TeamRatings.Add(TeamRating(Sport = "nfl", TeamAbbreviation = "KC", Source = "powerrankingsguru",
                                 Season = 2026, PowerRank = Nullable 18, PlayoffProbability = Nullable 0.005)) |> ignore
    let! _ = db.SaveChangesAsync()
    let! info = GameDataService(db, Clock()).GetTeamRankInfoAsync("nfl", "KC", Nullable 2026, Nullable 2, CancellationToken.None)
    Assert.True(info |> Option.bind (fun r -> r.PlayoffOdds) |> Option.isNone)
    Assert.True(info.Value.PowerRank.IsNone)
}

let private fixture () =
    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "kalshi-nfl-playoffs-2026.json"))

let private changeMarket team (change: JsonNode -> unit) =
    let root = JsonNode.Parse(fixture ())
    let market = (root["event"]["markets"]).AsArray() |> Seq.find (fun m -> (m["ticker"]).GetValue<string>() = "KXNFLPLAYOFF-27-" + team)
    change market
    root.ToJsonString()

let private refresh provider (clock: TimeProvider) status json = task {
    let requests = ResizeArray<string>()
    use handler =
        { new HttpMessageHandler() with
            override _.SendAsync(request, _) =
                requests.Add(request.RequestUri.PathAndQuery)
                let response = new HttpResponseMessage(status)
                response.Content <- new StringContent(
                    if request.RequestUri.AbsolutePath.Contains("/historical/") then """{"markets":[],"cursor":""}""" else json)
                Task.FromResult(response) }
    use http = new HttpClient(handler)
    let service = KalshiPlayoffRatingsService(http, NullLogger<KalshiPlayoffRatingsService>.Instance, provider, clock)
    let! count = service.RefreshAsync(NflRatingPolicy.seasonAt (clock.GetUtcNow().UtcDateTime), CancellationToken.None)
    Assert.InRange(requests.Count, 1, 2)
    Assert.Equal($"/trade-api/v2/events/KXNFLPLAYOFF-{(NflRatingPolicy.seasonAt (clock.GetUtcNow().UtcDateTime) + 1) % 100:D2}?with_nested_markets=true", requests[0])
    if requests.Count = 2 then Assert.Contains("/historical/markets?event_ticker=KXNFLPLAYOFF-", requests[1])
    return count
}

let private getInfo db clock team season =
    GameDataService(db, clock).GetTeamRankInfoAsync("nfl", team, Nullable season, Nullable 2, CancellationToken.None)

[<Fact>]
let ``Synthetic Kalshi event maps all 32 teams without normalizing across teams`` () = task {
    let connection, provider = createDatabase ()
    use _connection = connection
    use _provider = provider
    let clock = Clock()
    let! count = refresh provider clock HttpStatusCode.OK (fixture ())
    Assert.Equal(32, count)
    use scope = provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    let! rows = db.TeamRatings.ToListAsync()
    Assert.Equal(32, rows.Count)
    Assert.Equal(32, rows |> Seq.map (fun r -> r.TeamAbbreviation) |> Seq.distinct |> Seq.length)
    Assert.All(rows, fun r ->
        Assert.Equal(2026, r.Season)
        Assert.Equal(Nullable 2026, r.SourceSeason)
        Assert.Equal("kalshi", r.Source)
        Assert.Equal(now.UtcDateTime, r.FetchedAt))
    for team in ["JAX"; "WSH"; "LAR"; "NO"; "ARI"] do
        Assert.Contains(rows, fun r -> r.TeamAbbreviation = team)
    let! kc = getInfo db clock "KC" 2026
    let! buf = getInfo db clock "BUF" 2026
    Assert.Equal(0.62, kc.Value.PlayoffOdds.Value, 5)
    Assert.Equal(0.62, buf.Value.PlayoffOdds.Value, 5)
    Assert.True(kc.Value.PlayoffOdds.Value + buf.Value.PlayoffOdds.Value > 1.0)
    let! ranks = GameDataService(db, clock).GetDisplayRanksAsync("nfl", ["KC"; "BUF"], Nullable 2026, Nullable 2, CancellationToken.None)
    Assert.Equal(kc.Value.DisplayRank.Value, ranks["KC"])
    Assert.Equal(buf.Value.DisplayRank.Value, ranks["BUF"])

    clock.Now <- now.AddMinutes(5.0)
    let! updated = refresh provider clock HttpStatusCode.OK (changeMarket "KC" (fun m ->
        m["yes_bid_dollars"] <- JsonValue.Create("0.4000")
        m["yes_ask_dollars"] <- JsonValue.Create("0.4200")))
    Assert.Equal(32, updated)
    db.ChangeTracker.Clear()
    Assert.Equal(32, db.TeamRatings.Count())
    let! next = getInfo db clock "KC" 2026
    Assert.Equal(0.41, next.Value.PlayoffOdds.Value, 5)
}

[<Theory>]
[<InlineData("yes_bid_dollars", "")>]
[<InlineData("yes_ask_dollars", "")>]
[<InlineData("yes_bid_dollars", "NaN")>]
[<InlineData("yes_ask_dollars", "Infinity")>]
[<InlineData("yes_bid_dollars", "-0.01")>]
[<InlineData("yes_ask_dollars", "1.01")>]
[<InlineData("yes_bid_dollars", "0.6500")>]
[<InlineData("yes_ask_dollars", "0.8501")>]
[<InlineData("yes_bid_size_fp", "0.00")>]
[<InlineData("yes_ask_size_fp", "0.00")>]
[<InlineData("status", "closed")>]
[<InlineData("status", "inactive")>]
[<InlineData("close_time", "2026-09-13T17:00:00Z")>]
[<InlineData("event_ticker", "KXNFLPLAYOFF-26")>]
[<InlineData("ticker", "KXNFLPLAYOFF-26-KC")>]
let ``Unusable quote invalidates previous probability immediately`` (field: string) (value: string) = task {
    let connection, provider = createDatabase ()
    use _connection = connection
    use _provider = provider
    let clock = Clock()
    let! _ = refresh provider clock HttpStatusCode.OK (fixture ())
    clock.Now <- now.AddMinutes(5.0)
    let! count = refresh provider clock HttpStatusCode.OK (changeMarket "KC" (fun m -> m[field] <- JsonValue.Create(value)))
    Assert.Equal(31, count)
    use scope = provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    let! info = getInfo db clock "KC" 2026
    Assert.True(info.Value.PlayoffOdds.IsNone)
    Assert.Equal(Some "Playoff odds unavailable", info.Value.DisplayRank)
}

[<Theory>]
[<InlineData("0.8000", 0.70)>]
[<InlineData("0.8200", 0.71)>]
[<InlineData("0.8500", 0.725)>]
let ``Qualification spreads through 25 percentage points remain usable`` (ask: string) expected = task {
    let connection, provider = createDatabase ()
    use _connection = connection
    use _provider = provider
    let clock = Clock()
    let! count = refresh provider clock HttpStatusCode.OK (changeMarket "KC" (fun m -> m["yes_ask_dollars"] <- JsonValue.Create(ask)))
    Assert.Equal(32, count)
    use scope = provider.CreateScope()
    let! info = getInfo (scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()) clock "KC" 2026
    Assert.Equal(expected, info.Value.PlayoffOdds.Value, 5)
}

[<Theory>]
[<InlineData("yes", 1.0, "Clinched playoffs")>]
[<InlineData("no", 0.0, "Missed playoffs")>]
let ``Settled markets use outcomes even with no quotes`` (result: string) (expected: float) (label: string) = task {
    let connection, provider = createDatabase ()
    use _connection = connection
    use _provider = provider
    let clock = Clock()
    let! _ = refresh provider clock HttpStatusCode.OK (changeMarket "KC" (fun m ->
        m["status"] <- JsonValue.Create("finalized")
        m["result"] <- JsonValue.Create(result)
        m["yes_bid_dollars"] <- null
        m["yes_ask_dollars"] <- null
        m["close_time"] <- JsonValue.Create("2026-09-01T00:00:00Z")))
    use scope = provider.CreateScope()
    let! info = getInfo (scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()) clock "KC" 2026
    Assert.Equal(expected, info.Value.PlayoffOdds.Value)
    Assert.Equal(Some label, info.Value.DisplayRank)
}

[<Theory>]
[<InlineData("sub_title", "2025-26")>]
[<InlineData("event_ticker", "KXNFLPLAYOFF-26")>]
[<InlineData("series_ticker", "KXNFLGAME")>]
let ``Wrong event metadata never enters current season`` (field: string) (value: string) = task {
    let connection, provider = createDatabase ()
    use _connection = connection
    use _provider = provider
    let root = JsonNode.Parse(fixture ())
    root["event"][field] <- JsonValue.Create(value)
    let! count = refresh provider (Clock()) HttpStatusCode.OK (root.ToJsonString())
    Assert.Equal(0, count)
    use scope = provider.CreateScope()
    Assert.Empty(scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().TeamRatings)
}

[<Theory>]
[<InlineData(1, 2025)>]
[<InlineData(2, 2025)>]
[<InlineData(3, 2026)>]
[<InlineData(8, 2026)>]
[<InlineData(9, 2026)>]
let ``Refresh selects NFL league year including postseason and preseason`` month season = task {
    let connection, provider = createDatabase ()
    use _connection = connection
    use _provider = provider
    let clock = Clock()
    clock.Now <- DateTimeOffset(2026, month, 15, 0, 0, 0, TimeSpan.Zero)
    Assert.Equal(season, NflRatingPolicy.seasonAt clock.Now.UtcDateTime)
    let json = (fixture ()).Replace("KXNFLPLAYOFF-27", $"KXNFLPLAYOFF-{(season + 1) % 100:D2}").Replace("2026-27", $"{season}-{(season + 1) % 100:D2}")
    let! count = refresh provider clock HttpStatusCode.OK json
    Assert.Equal(32, count)
}

[<Fact>]
let ``Outage retains last good quotes until exactly one hour without refreshing their age`` () = task {
    let connection, provider = createDatabase ()
    use _connection = connection
    use _provider = provider
    let clock = Clock()
    let! _ = refresh provider clock HttpStatusCode.OK (fixture ())
    clock.Now <- now.AddMinutes(59.0)
    let! failed = refresh provider clock HttpStatusCode.ServiceUnavailable "unavailable"
    Assert.Equal(0, failed)
    use scope = provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    let! beforeExpiry = getInfo db clock "KC" 2026
    Assert.Equal(Some 0.62, beforeExpiry.Value.PlayoffOdds)
    Assert.All(db.TeamRatings.ToList(), fun r -> Assert.Equal(now.UtcDateTime, r.FetchedAt))
    clock.Now <- now.AddHours(1.0)
    let! expired = getInfo db clock "KC" 2026
    Assert.True(expired.Value.PlayoffOdds.IsNone)
    clock.Now <- now.AddSeconds(-1.0)
    let! future = getInfo db clock "KC" 2026
    Assert.True(future.Value.PlayoffOdds.IsNone)
}

[<Fact>]
let ``Missing and duplicate team markets are unavailable rather than stale`` () = task {
    let connection, provider = createDatabase ()
    use _connection = connection
    use _provider = provider
    let root = JsonNode.Parse(fixture ())
    let markets = (root["event"]["markets"]).AsArray()
    let kc = markets |> Seq.find (fun m -> (m["ticker"]).GetValue<string>() = "KXNFLPLAYOFF-27-KC")
    markets.Remove(kc) |> ignore
    let buf = markets |> Seq.find (fun m -> (m["ticker"]).GetValue<string>() = "KXNFLPLAYOFF-27-BUF")
    markets.Add(buf.DeepClone())
    let clock = Clock()
    let! _ = refresh provider clock HttpStatusCode.OK (fixture ())
    let! count = refresh provider clock HttpStatusCode.OK (root.ToJsonString())
    Assert.Equal(30, count)
    use scope = provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    for team in ["KC"; "BUF"] do
        let! info = getInfo db clock team 2026
        Assert.True(info.Value.PlayoffOdds.IsNone)
}

[<Fact>]
let ``Display and hype use the same season and expiry and never fall back to other sources`` () = task {
    let connection, provider = createDatabase ()
    use _connection = connection
    use _provider = provider
    let clock = Clock()
    let! _ = refresh provider clock HttpStatusCode.OK (fixture ())
    use scope = provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    for source in ["powerrankingsguru"; "playoffstatus"] do
        for team in ["KC"; "BUF"] do
            db.TeamRatings.Add(TeamRating(Sport = "nfl", TeamAbbreviation = team, Source = source,
                                         Season = 2026, PowerRank = Nullable 18, PlayoffProbability = Nullable 0.005,
                                         ChampionshipProbability = Nullable 0.995, FetchedAt = now.AddMinutes(1.0).UtcDateTime)) |> ignore
    db.TeamRatings.Add(TeamRating(Sport = "nfl", TeamAbbreviation = "KC", Source = "kalshi", Season = 2025,
                                 SourceSeason = Nullable 2025, PlayoffProbability = Nullable 0.99, FetchedAt = now.UtcDateTime)) |> ignore
    let game = Game(Id = "test-game", Sport = "nfl", HomeTeam = "Chiefs", AwayTeam = "Bills",
                    HomeTeamAbbreviation = "KC", AwayTeamAbbreviation = "BUF", Status = "scheduled",
                    SeasonYear = Nullable 2026, SeasonType = Nullable 2, ScheduledStart = now.AddHours(2.0).UtcDateTime)
    db.Games.Add(game) |> ignore
    db.PregameOdds.Add(PregameOdds(GameId = game.Id, HomeWinProbability = 0.5, AwayWinProbability = 0.5)) |> ignore
    let! _ = db.SaveChangesAsync()
    let hype = HypeScoreService(provider, NullLogger<HypeScoreService>.Instance, clock)
    let! home = getInfo db clock "KC" 2026
    let! away = getInfo db clock "BUF" 2026
    let! components = hype.GetHypeComponentsAsync(game)
    Assert.Equal(home.Value.PlayoffOdds |> Option.toNullable, components.HomePlayoffOdds)
    Assert.Equal(away.Value.PlayoffOdds |> Option.toNullable, components.AwayPlayoffOdds)
    Assert.True(home.Value.PowerRank.IsNone)

    game.SeasonYear <- Nullable 2025
    let! historical = hype.GetHypeComponentsAsync(game)
    Assert.Equal(Nullable 0.99, historical.HomePlayoffOdds)
    Assert.False(historical.AwayPlayoffOdds.HasValue)
    Assert.False(historical.StakesAvailable)
    game.SeasonYear <- Nullable 2026
    clock.Now <- now.AddHours(1.0)
    let! expired = hype.GetHypeComponentsAsync(game)
    let! expiredRank = getInfo db clock "KC" 2026
    Assert.False(expired.HomePlayoffOdds.HasValue)
    Assert.False(expired.AwayPlayoffOdds.HasValue)
    Assert.True(expiredRank.Value.PlayoffOdds.IsNone)
    Assert.False(expired.StakesAvailable)
    let html = GameDetailRenderer.Render game (ResizeArray<OddsSnapshot>()) None expired (Nullable()) expiredRank expiredRank
               |> List.map XmlNodeRenderer.renderNode |> String.concat ""
    Assert.Contains("Unavailable", html)
}

[<Fact>]
let ``Only verified matching-season power rankings are displayed and postseason still uses seeds`` () = task {
    let connection, provider = createDatabase ()
    use _connection = connection
    use _provider = provider
    use scope = provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    db.TeamRatings.Add(TeamRating(Sport = "nfl", TeamAbbreviation = "KC", Source = "powerrankingsguru",
                                 Season = 2026, SourceSeason = Nullable 2026, PowerRank = Nullable 3,
                                 PlayoffProbability = Nullable 0.99, FetchedAt = now.UtcDateTime)) |> ignore
    db.TeamRatings.Add(TeamRating(Sport = "nfl", TeamAbbreviation = "KC", Source = "powerrankingsguru",
                                 Season = 2025, SourceSeason = Nullable 2025, PowerRank = Nullable 18,
                                 FetchedAt = now.AddMinutes(1.0).UtcDateTime)) |> ignore
    db.TeamStandings.Add(TeamStanding(Sport = "nfl", TeamAbbreviation = "KC", Season = 2026, ConferenceRank = Nullable 2)) |> ignore
    db.TeamStandings.Add(TeamStanding(Sport = "nfl", TeamAbbreviation = "KC", Season = 2025, ConferenceRank = Nullable 8,
                                     FetchedAt = now.AddMinutes(1.0).UtcDateTime)) |> ignore
    let! _ = db.SaveChangesAsync()
    let clock = Clock()
    let! info = getInfo db clock "KC" 2026
    Assert.Equal(Some 3, info.Value.PowerRank)
    Assert.True(info.Value.PlayoffOdds.IsNone)
    let! seeds = GameDataService(db, clock).GetDisplayRanksAsync("nfl", ["KC"], Nullable 2026, Nullable 3, CancellationToken.None)
    Assert.Equal("#2 seed", seeds["KC"])
}

[<Fact>]
let ``Current source page verifies NFL power ranking season`` () = task {
    let connection, provider = createDatabase ()
    use _connection = connection
    use _provider = provider
    use handler =
        { new HttpMessageHandler() with
            override _.SendAsync(_, _) =
                let response = new HttpResponseMessage(HttpStatusCode.OK)
                response.Content <- new StringContent(rankingsHtml 2026)
                Task.FromResult(response) }
    use http = new HttpClient(handler)
    let clock = Clock()
    let! count = PowerRankingsGuruService(http, NullLogger<PowerRankingsGuruService>.Instance, provider, clock).RefreshNflRatingsAsync()
    Assert.Equal(1, count)
    use scope = provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    Assert.Equal(Nullable 2026, db.TeamRatings.Single().SourceSeason)
    let! info = getInfo db clock "KC" 2026
    Assert.Equal(Some 18, info.Value.PowerRank)
    Assert.True(info.Value.PlayoffOdds.IsNone)
}

[<Fact>]
let ``Non NFL power rankings remain separate from Kalshi-only qualification`` () = task {
    let connection, provider = createDatabase ()
    use _connection = connection
    use _provider = provider
    use scope = provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    db.TeamRatings.Add(TeamRating(Sport = "nba", TeamAbbreviation = "BOS", Source = "powerrankingsguru",
                                 Season = 2026, SourceSeason = Nullable 2026, FetchedAt = now.UtcDateTime,
                                 PowerRank = Nullable 3, PlayoffProbability = Nullable 0.75)) |> ignore
    let! _ = db.SaveChangesAsync()
    let! info = GameDataService(db, Clock()).GetTeamRankInfoAsync("nba", "BOS", Nullable 2026, Nullable 2, CancellationToken.None)
    Assert.Equal(Some 3, info.Value.PowerRank)
    Assert.True(info.Value.PlayoffOdds.IsNone)
    Assert.Contains("Playoff odds unavailable", info.Value.DisplayRank.Value)
}

type private TimerClock() =
    inherit TimeProvider()
    let mutable tick = fun () -> ()
    member val DueTime = TimeSpan.Zero with get, set
    member val Period = TimeSpan.Zero with get, set
    member _.Tick() = tick ()
    override _.GetUtcNow() = now
    override this.CreateTimer(callback, state, dueTime, period) =
        this.DueTime <- dueTime
        this.Period <- period
        tick <- fun () -> callback.Invoke(state)
        { new ITimer with
            member _.Change(_, _) = true
            member _.Dispose() = ()
            member _.DisposeAsync() = ValueTask.CompletedTask }

[<Fact>]
let ``Playoff worker refreshes at startup and each five minute tick even after failures`` () = task {
    let clock = TimerClock()
    let first = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    let second = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    let mutable calls = 0
    let requestsPerCycle = PlayoffRatingPolicy.sports |> List.sumBy (fun sport -> PlayoffRatingPolicy.refreshSeasons sport now.UtcDateTime |> List.length)
    use handler =
        { new HttpMessageHandler() with
            override _.SendAsync(_, _) =
                let count = Interlocked.Increment(&calls)
                if count = requestsPerCycle then first.TrySetResult() |> ignore
                if count = requestsPerCycle * 2 then second.TrySetResult() |> ignore
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)) }
    use http = new HttpClient(handler)
    use connection = new SqliteConnection("Data Source=:memory:")
    connection.Open()
    let services = ServiceCollection()
    services.AddDbContext<ExcitementDbContext>(fun options -> options.UseSqlite(connection) |> ignore) |> ignore
    services.AddLogging() |> ignore
    services.AddSingleton<TimeProvider>(clock) |> ignore
    services.AddTransient<KalshiPlayoffRatingsService>(fun sp ->
        KalshiPlayoffRatingsService(http, NullLogger<KalshiPlayoffRatingsService>.Instance, sp, clock)) |> ignore
    use provider = services.BuildServiceProvider()
    use scope = provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    db.Database.EnsureCreated() |> ignore
    db.CollegeTeams.Add(CollegeTeam(Season = CollegeFootballPolicy.SeasonAt(now.UtcDateTime), EspnId = "2483", Abbreviation = "ORE", Name = "Oregon")) |> ignore
    db.SaveChanges() |> ignore
    use worker = new KalshiPlayoffRefreshBackgroundService(provider, clock, NullLogger<KalshiPlayoffRefreshBackgroundService>.Instance)
    do! worker.StartAsync(CancellationToken.None)
    try
        do! first.Task.WaitAsync(TimeSpan.FromSeconds(5.0))
        Assert.Equal(TimeSpan.FromMinutes(5.0), clock.DueTime)
        Assert.Equal(TimeSpan.FromMinutes(5.0), clock.Period)
        Assert.Equal(requestsPerCycle, calls)
        clock.Tick()
        do! second.Task.WaitAsync(TimeSpan.FromSeconds(5.0))
    finally
        worker.StopAsync(CancellationToken.None).GetAwaiter().GetResult()
    Assert.Equal(requestsPerCycle * 2, calls)
}
