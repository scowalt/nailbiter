module PlayoffPolicyTests

open System
open System.Linq
open System.Text.Json
open System.Threading
open Microsoft.Data.Sqlite
open Microsoft.EntityFrameworkCore
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open backend.Data
open backend.Models
open backend.Services
open Backend.Rendering
open Falco.Markup

let private now = DateTimeOffset(2026, 9, 14, 18, 0, 0, TimeSpan.Zero)

type private Clock() =
    inherit TimeProvider()
    member val Now = now with get, set
    override this.GetUtcNow() = this.Now

let private rating sport season source probability =
    TeamRating(Sport = sport, TeamAbbreviation = "BOS", Season = season, SourceSeason = Nullable season,
               Source = source, PlayoffProbability = probability, FetchedAt = now.UtcDateTime)

let private kalshiFinalEvidence sport season team probability observedAt =
    let ticker = $"KXNHLPLAYOFF-{season % 100:D2}"
    PlayoffQuoteEvidence(Version = 1, ValidationPolicy = (if sport = "nhl" then "kalshi-qualification-v1" else "kalshi-test-final-v1"),
                         Provider = PlayoffRatingPolicy.kalshiSource, Sport = sport, Team = team,
                         Season = season, EventId = (if sport = "nhl" then ticker else $"{sport}-{season}"),
                         MarketId = (if sport = "nhl" then ticker + "-" + team else $"{sport}-{season}-{team}"),
                         ContractVerified = true, State = "final", ReasonCode = "accepted",
                         ObservedAt = Nullable observedAt, EvidenceAt = Nullable observedAt, ResolutionAt = Nullable observedAt,
                         YesPayout = Nullable(decimal probability), NoPayout = Nullable(1M - decimal probability),
                         ResolutionReference = "synthetic-test-final")
    |> JsonSerializer.Serialize

[<Theory>]
[<InlineData("nfl")>]
[<InlineData("nba")>]
[<InlineData("nhl")>]
[<InlineData("mlb")>]
let ``Newest Kalshi snapshot wins before null verification and expiry checks`` sport =
    let old = rating sport 2026 "kalshi" (Nullable 0.7)
    old.FetchedAt <- now.AddMinutes(-10.0).UtcDateTime
    let latest = rating sport 2026 "kalshi" (Nullable())
    let other = rating sport 2026 "playoffstatus" (Nullable 0.99)
    let wrongSeason = rating sport 2027 "kalshi" (Nullable 0.98)
    let rows = [old; other; wrongSeason; latest]
    let select () = PlayoffRatingPolicy.playoffProbability sport 2026 now.UtcDateTime rows
    Assert.True((select ()).IsNone)
    latest.PlayoffProbability <- Nullable 0.0
    Assert.Equal(Some 0.0, select ())
    latest.SourceSeason <- Nullable()
    Assert.True((select ()).IsNone)
    latest.SourceSeason <- Nullable 2026
    latest.FetchedAt <- now.AddSeconds(1.0).UtcDateTime
    Assert.True((select ()).IsNone)

[<Theory>]
[<InlineData("nfl")>]
[<InlineData("nba")>]
[<InlineData("nhl")>]
[<InlineData("mlb")>]
let ``Only confirmed binary outcomes survive expiry`` sport =
    let row = rating sport 2026 "kalshi" (Nullable 1.0)
    let later = now.AddHours(1.0).UtcDateTime
    Assert.True((PlayoffRatingPolicy.playoffProbability sport 2026 later [row]).IsNone)
    row.PlayoffProbabilityIsFinal <- true
    if sport = "nhl" then row.PlayoffEvidenceJson <- kalshiFinalEvidence sport 2026 "BOS" 1.0 row.FetchedAt
    Assert.Equal(Some 1.0, PlayoffRatingPolicy.playoffProbability sport 2026 (later.AddYears(2)) [row])
    row.PlayoffProbability <- Nullable 0.0
    if sport = "nhl" then row.PlayoffEvidenceJson <- kalshiFinalEvidence sport 2026 "BOS" 0.0 row.FetchedAt
    Assert.Equal(Some 0.0, PlayoffRatingPolicy.playoffProbability sport 2026 later [row])
    for invalid in [0.5; -0.1; 1.1; Double.NaN; Double.PositiveInfinity] do
        row.PlayoffProbability <- Nullable invalid
        Assert.True((PlayoffRatingPolicy.playoffProbability sport 2026 now.UtcDateTime [row]).IsNone)
    row.PlayoffProbability <- Nullable 1.0
    row.Source <- "powerrankingsguru"
    Assert.True((PlayoffRatingPolicy.playoffProbability sport 2026 later [row]).IsNone)

[<Theory>]
[<InlineData("nfl", 2, 2025)>]
[<InlineData("nfl", 3, 2026)>]
[<InlineData("nba", 1, 2026)>]
[<InlineData("nba", 9, 2026)>]
[<InlineData("nba", 10, 2027)>]
[<InlineData("nhl", 1, 2026)>]
[<InlineData("nhl", 10, 2027)>]
[<InlineData("mlb", 1, 2026)>]
[<InlineData("mlb", 12, 2026)>]
let ``Season fallback respects each league naming convention`` sport month season =
    Assert.Equal(season, PlayoffRatingPolicy.seasonAt sport (DateTime(2026, month, 1)))

[<Fact>]
let ``Offseason refreshes keep ending and upcoming NBA NHL MLB seasons separate`` () =
    for sport in ["nba"; "nhl"] do
        Assert.Equal<int list>([2026], PlayoffRatingPolicy.refreshSeasons sport (DateTime(2026, 6, 30)))
        Assert.Equal<int list>([2026; 2027], PlayoffRatingPolicy.refreshSeasons sport (DateTime(2026, 7, 1)))
        Assert.Equal<int list>([2027], PlayoffRatingPolicy.refreshSeasons sport (DateTime(2026, 10, 1)))
    Assert.Equal<int list>([2026; 2027], PlayoffRatingPolicy.refreshSeasons "mlb" (DateTime(2026, 11, 1)))

let private createDatabase () =
    let connection = new SqliteConnection("Data Source=:memory:")
    connection.Open()
    let services = ServiceCollection()
    services.AddDbContext<ExcitementDbContext>(fun options -> options.UseSqlite(connection) |> ignore) |> ignore
    let provider = services.BuildServiceProvider()
    use scope = provider.CreateScope()
    scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.EnsureCreated() |> ignore
    connection, provider

[<Theory>]
[<InlineData("nba")>]
[<InlineData("nhl")>]
[<InlineData("mlb")>]
let ``Display and hype agree on game season source expiry and unavailable missing stakes`` sport = task {
    let connection, provider = createDatabase ()
    use _connection = connection
    use _provider = provider
    use scope = provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    let clock = Clock()
    for team in ["BOS"; "NYK"] do
        let kalshi = rating sport 2025 "kalshi" (Nullable 0.63)
        kalshi.TeamAbbreviation <- team
        db.TeamRatings.Add(kalshi) |> ignore
        let other = rating sport 2025 "powerrankingsguru" (Nullable 0.01)
        other.TeamAbbreviation <- team
        other.PowerRank <- Nullable 3
        other.BpiRating <- Nullable 8.0
        other.ChampionshipProbability <- Nullable 0.1
        other.FetchedAt <- now.AddSeconds(1.0).UtcDateTime
        db.TeamRatings.Add(other) |> ignore
        let current = rating sport 2026 "kalshi" (Nullable 0.99)
        current.TeamAbbreviation <- team
        db.TeamRatings.Add(current) |> ignore
    let game = Game(Id = "season-game", Sport = sport, HomeTeam = "Boston", AwayTeam = "New York",
                    HomeTeamAbbreviation = "BOS", AwayTeamAbbreviation = "NYK", Status = "scheduled",
                    SeasonYear = Nullable 2025, SeasonType = Nullable 2, ScheduledStart = now.UtcDateTime)
    db.Games.Add(game) |> ignore
    db.PregameOdds.Add(PregameOdds(GameId = game.Id, HomeWinProbability = 0.5, AwayWinProbability = 0.5)) |> ignore
    db.TeamStandings.Add(TeamStanding(Sport = sport, TeamAbbreviation = "BOS", Season = 2025,
                                     ConferenceRank = Nullable 2, FetchedAt = now.UtcDateTime)) |> ignore
    db.TeamStandings.Add(TeamStanding(Sport = sport, TeamAbbreviation = "BOS", Season = 2026,
                                     ConferenceRank = Nullable 8, FetchedAt = now.AddMinutes(1.0).UtcDateTime)) |> ignore
    let! _ = db.SaveChangesAsync()
    let data = GameDataService(db, clock)
    let hype = HypeScoreService(provider, NullLogger<HypeScoreService>.Instance, clock)
    let! info = data.GetTeamRankInfoAsync(sport, "BOS", game.SeasonYear, game.SeasonType, CancellationToken.None)
    let! components = hype.GetHypeComponentsAsync(game)
    Assert.Equal(Some 0.63, info.Value.PlayoffOdds)
    Assert.Equal(Nullable 0.63, components.HomePlayoffOdds)
    Assert.Equal(Nullable 0.63, components.AwayPlayoffOdds)
    Assert.True(info.Value.PowerRank.IsNone)  
    Assert.False(components.QualityAvailable)
    Assert.False(components.HomeTeamElo.HasValue)
    Assert.Equal(Some now.UtcDateTime, info.Value.PlayoffFetchedAt)
    let html = GameDetailRenderer.Render game (ResizeArray<OddsSnapshot>()) None components (Nullable()) info info
               |> List.map XmlNodeRenderer.renderNode |> String.concat ""
    if sport = "nhl" then Assert.DoesNotContain("Kalshi", html) else Assert.Contains("Kalshi", html)
    Assert.DoesNotContain("2026-09-14 18:00 UTC", html)
    Assert.DoesNotContain("source season", html)

    clock.Now <- now.AddHours(1.0)
    let! expired = hype.GetHypeComponentsAsync(game)
    let! expiredInfo = data.GetTeamRankInfoAsync(sport, "BOS", game.SeasonYear, game.SeasonType, CancellationToken.None)
    Assert.False(expired.HomePlayoffOdds.HasValue)
    Assert.False(expired.AwayPlayoffOdds.HasValue)
    Assert.True(expiredInfo.Value.PlayoffOdds.IsNone)
    Assert.False(expired.StakesAvailable)
    Assert.True(expiredInfo.Value.PowerRank.IsNone)
    Assert.Contains("Playoff odds unavailable", expiredInfo.Value.DisplayRank.Value)

    game.SeasonType <- Nullable 3
    let! postseason = hype.GetHypeComponentsAsync(game)
    Assert.False(postseason.StakesAvailable)  
    let! seeds = data.GetDisplayRanksAsync(sport, ["BOS"], game.SeasonYear, game.SeasonType, CancellationToken.None)
    Assert.Equal("#2 seed", seeds["BOS"])
}

[<Theory>]
[<InlineData("nfl")>]
[<InlineData("nba")>]
[<InlineData("nhl")>]
[<InlineData("mlb")>]
let ``Data-only scoring cannot use another providers qualification estimates`` sport =
    use provider = ServiceCollection().BuildServiceProvider()
    let clock = Clock()
    let service = HypeScoreService(provider, NullLogger<HypeScoreService>.Instance, clock)
    let game = Game(Id = "direct", Sport = sport, HomeTeam = "Boston", AwayTeam = "New York",
                    HomeTeamAbbreviation = "BOS", AwayTeamAbbreviation = "NYK",
                    SeasonYear = Nullable 2026, SeasonType = Nullable 2, ScheduledStart = now.UtcDateTime)
    let other = rating sport 2026 "playoffstatus" (Nullable 0.7)
    let kalshi = rating sport 2026 "kalshi" (Nullable 0.6)
    kalshi.TeamAbbreviation <- "NYK"
    let odds = PregameOdds(GameId = game.Id, HomeWinProbability = 0.5, AwayWinProbability = 0.5)
    let result = service.CalculateFromData(game, other, kalshi, odds, null, null)
    Assert.False(result.HomePlayoffOdds.HasValue)
    Assert.Equal(Nullable 0.6, result.AwayPlayoffOdds)
    Assert.False(result.StakesAvailable)
    Assert.Equal(Nullable 0.7, other.PlayoffProbability)
    kalshi.TeamAbbreviation <- "BOS"
    let wrongTeam = service.CalculateFromData(game, other, kalshi, odds, null, null)
    Assert.False(wrongTeam.AwayPlayoffOdds.HasValue)
    game.SeasonType <- Nullable 3
    let missingAway = service.CalculateFromData(game, kalshi, null, odds, null, null)
    Assert.Equal(Nullable 0.6, missingAway.HomePlayoffOdds)
    Assert.False(missingAway.AwayPlayoffOdds.HasValue)
    Assert.False(missingAway.StakesAvailable)
    game.SeasonType <- Nullable 2
    Assert.False(service.CalculateFromData(game, kalshi, null, odds, null, null).StakesAvailable)

[<Theory>]
[<InlineData("nfl")>]
[<InlineData("nba")>]
[<InlineData("nhl")>]
[<InlineData("mlb")>]
let ``Missing whole-team ratings produce unavailable odds and stakes`` sport = task {
    let connection, provider = createDatabase ()
    use _connection = connection
    use _provider = provider
    use scope = provider.CreateScope()
    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
    let game = Game(Id = "missing", Sport = sport, HomeTeam = "Boston", AwayTeam = "New York",
                    HomeTeamAbbreviation = "BOS", AwayTeamAbbreviation = "NYK", Status = "scheduled",
                    SeasonYear = Nullable 2026, SeasonType = Nullable 2, ScheduledStart = now.UtcDateTime)
    db.Games.Add(game) |> ignore
    db.PregameOdds.Add(PregameOdds(GameId = game.Id, HomeWinProbability = 0.5, AwayWinProbability = 0.5)) |> ignore
    let! _ = db.SaveChangesAsync()
    let! result = HypeScoreService(provider, NullLogger<HypeScoreService>.Instance, Clock()).GetHypeComponentsAsync(game)
    Assert.False(result.StakesAvailable)
    Assert.False(result.HomePlayoffOdds.HasValue)
    let! info = GameDataService(db, Clock()).GetTeamRankInfoAsync(sport, "BOS", Nullable 2026, Nullable 2, CancellationToken.None)
    Assert.Equal(Some "Playoff odds unavailable", info.Value.DisplayRank)
}
