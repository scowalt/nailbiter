namespace backend.Services

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.EntityFrameworkCore
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open backend.Data
open backend.Models
open backend.Services.Espn
open backend.Services.Kalshi
open Backend.FSharp

type PregameOddsService [<ActivatorUtilitiesConstructor>]
    (serviceProvider: IServiceProvider,
     logger: ILogger<PregameOddsService>,
     espnService: EspnOddsService,
     timeProvider: TimeProvider,
     kalshiService: KalshiOddsService) =

    static let supportedSports = [| "nba"; "nfl"; "nhl"; "mlb"; "world_cup" |]

    static let tryParseMoneyline (odds: string) =
        OddsConverter.TryParseMoneyline(odds)

    new(serviceProvider, logger, espnService, timeProvider) =
        PregameOddsService(serviceProvider, logger, espnService, timeProvider, Unchecked.defaultof<KalshiOddsService>)

    new(serviceProvider, logger, espnService) = PregameOddsService(serviceProvider, logger, espnService, TimeProvider.System)

    member this.RefreshPregameOddsAsync(?ct: CancellationToken) : Task<int> =
        let ct = defaultArg ct CancellationToken.None

        task {
            let mutable totalUpdated = 0

            for sport in supportedSports do
                let! updated = this.RefreshPregameOddsForSportAsync(sport, ct)
                totalUpdated <- totalUpdated + updated

            logger.LogInformation("Refreshed pregame odds for {Count} games across all sports", totalUpdated)
            return totalUpdated
        }

    member this.RefreshPregameOddsForSportAsync(sport: string, ?ct: CancellationToken) : Task<int> =
        let ct = defaultArg ct CancellationToken.None

        task {
            let mutable updatedCount = 0
            let now = timeProvider.GetUtcNow().UtcDateTime

            use scope = serviceProvider.CreateScope()
            let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()

            for dayOffset in 0 .. 7 do
                let date = now.AddDays(float dayOffset).ToString("yyyyMMdd")
                let! scoreboard = espnService.FetchScoreboardAsync(sport, date, ct)

                if not (isNull (box scoreboard)) then
                    for espnEvent in scoreboard.Events do
                        if espnEvent.Status.Type.State = "pre" then
                            let competition = espnEvent.Competitions |> seqOfObj |> Seq.tryHead

                            match competition with
                            | None -> ()
                            | Some competition ->

                            let homeCompetitor = competition.Competitors |> seqOfObj |> Seq.tryFind (fun c -> c.HomeAway = "home")
                            let awayCompetitor = competition.Competitors |> seqOfObj |> Seq.tryFind (fun c -> c.HomeAway = "away")

                            match homeCompetitor, awayCompetitor with
                            | None, _ | _, None -> ()
                            | Some homeComp, Some awayComp ->

                            let sportLower = sport.ToLower()
                            let isWorldCup = sportLower = backend.GraphQL.Types.SportExtensions.ToDbValue(backend.GraphQL.Types.Sport.WORLD_CUP)
                            let seasonType = EspnSeasonMetadata.seasonType espnEvent.Season scoreboard

                            let verifiedSeason =
                                if not (isNull (box espnEvent.Season))
                                   && EspnPregameStatus.IsUnstarted(sportLower, espnEvent.Status.Type.Name,
                                        espnEvent.Status.Type.State, espnEvent.Status.Type.Completed.GetValueOrDefault(), Nullable espnEvent.Status.Period)
                                   && (competition.Competitors |> seqOfObj |> Seq.forall (fun c -> isNull c.Score || c.Score = "0")) then
                                    Nullable espnEvent.Season.Year
                                else Nullable()

                            let espnOddsData =
                                if isWorldCup then
                                    match EspnOddsParsing.tryFindCompleteThreeWayOdds competition.Odds with
                                    | Some threeWay ->
                                        let struct (homeProb, drawProb, awayProb) =
                                            OddsConverter.ToNormalizedThreeWayProbabilities(threeWay.HomeMoneyline, threeWay.DrawMoneyline, threeWay.AwayMoneyline)
                                        Some (threeWay.HomeMoneyline, Nullable<int>(threeWay.DrawMoneyline), threeWay.AwayMoneyline, homeProb, Nullable<float>(drawProb), awayProb, threeWay.Spread, threeWay.OverUnder)
                                    | None -> None
                                else
                                    match competition.Odds |> seqOfObj |> Seq.tryHead with
                                    | None -> None
                                    | Some odds when
                                        isNull (box odds.Moneyline)
                                        || isNull (box odds.Moneyline.Home)
                                        || isNull (box odds.Moneyline.Home.Close)
                                        || isNull odds.Moneyline.Home.Close.Odds
                                        || isNull (box odds.Moneyline.Away)
                                        || isNull (box odds.Moneyline.Away.Close)
                                        || isNull odds.Moneyline.Away.Close.Odds -> None
                                    | Some odds ->
                                        match tryParseMoneyline odds.Moneyline.Home.Close.Odds, tryParseMoneyline odds.Moneyline.Away.Close.Odds with
                                        | Some homeMoneyline, Some awayMoneyline ->
                                            let struct (homeProb, awayProb) = OddsConverter.ToNormalizedProbabilities(homeMoneyline, awayMoneyline)
                                            Some (homeMoneyline, Nullable<int>(), awayMoneyline, homeProb, Nullable<float>(), awayProb, odds.Spread, odds.OverUnder)
                                        | _ -> None

                            let mutable kalshiFetchFailed = false
                            let! kalshiOdds = task {
                                if backend.Scoring.ProHypePolicy.Supports(sportLower) && verifiedSeason.HasValue
                                   && espnEvent.Date > now && not (isNull (box kalshiService)) then
                                    try
                                        let! quote = kalshiService.GetPregameOddsAsync(sportLower, homeComp.Team.Abbreviation,
                                                                                      awayComp.Team.Abbreviation, espnEvent.Date, now, ct)
                                        return if isNull (box quote) then None else Some quote
                                    with
                                    | :? OperationCanceledException when ct.IsCancellationRequested ->
                                        return raise (OperationCanceledException(ct))
                                    | ex ->
                                        kalshiFetchFailed <- true
                                        logger.LogWarning(ex, "Failed to fetch Kalshi pregame odds for {Game}", espnEvent.ShortName)
                                        return None
                                else return None
                            }
                            let source, oddsData =
                                match kalshiOdds with
                                | Some quote ->
                                    "kalshi", Some (quote.HomeMoneyline, Nullable<int>(), quote.AwayMoneyline,
                                                    quote.HomeWinProbability, Nullable<float>(), quote.AwayWinProbability,
                                                    Nullable<float>(), Nullable<float>())
                                | None -> "espn", espnOddsData

                            match oddsData with
                            | None ->
                                if backend.Scoring.ProHypePolicy.Supports(sportLower) then
                                    let! previous = db.PregameOdds.FindAsync([| espnEvent.Id :> obj |], ct)
                                    if not (isNull previous) && not (kalshiFetchFailed && previous.Source = "kalshi") then
                                        previous.SourceSeason <- Nullable()
                                        previous.ApplicableStart <- Nullable()
                                        previous.FetchedAt <- now
                                logger.LogDebug("No complete moneyline odds for {Game}", espnEvent.ShortName)
                            | Some (homeMoneyline, drawMoneyline, awayMoneyline, homeProb, drawProb, awayProb, spread, overUnder) ->

                            let! rawIdGame = db.Games.FindAsync([| espnEvent.Id :> obj |], ct)
                            let gameId =
                                if isWorldCup && not (isNull rawIdGame) && rawIdGame.Sport <> sportLower then
                                    $"worldcup-{espnEvent.Id}"
                                else
                                    espnEvent.Id

                            let! game =
                                if gameId = espnEvent.Id then
                                    Task.FromResult(rawIdGame)
                                else
                                    db.Games.FindAsync([| gameId :> obj |], ct).AsTask()

                            if isNull game then
                                let newGame =
                                    Game(
                                        Id = gameId,
                                        Sport = sportLower,
                                        HomeTeam = homeComp.Team.DisplayName,
                                        AwayTeam = awayComp.Team.DisplayName,
                                        HomeTeamAbbreviation = homeComp.Team.Abbreviation,
                                        AwayTeamAbbreviation = awayComp.Team.Abbreviation,
                                        ScheduledStart = espnEvent.Date,
                                        SeasonYear = (if isNull (box espnEvent.Season) then Nullable() else Nullable espnEvent.Season.Year),
                                        SeasonType = seasonType,
                                        HomeTeamId = homeComp.Team.Id,
                                        AwayTeamId = awayComp.Team.Id,
                                        Status = "scheduled"
                                    )

                                db.Games.Add(newGame) |> ignore
                                let! _ = db.SaveChangesAsync(ct)  
                                ()
                            elif seasonType.HasValue then
                                game.SeasonType <- seasonType

                            let! existing = db.PregameOdds.FindAsync([| gameId :> obj |], ct)

                            if not (isNull existing) then
                                existing.HomeMoneyline <- Nullable homeMoneyline
                                existing.AwayMoneyline <- Nullable awayMoneyline
                                existing.DrawMoneyline <- drawMoneyline
                                existing.Spread <- spread
                                existing.OverUnder <- overUnder
                                existing.HomeWinProbability <- homeProb
                                existing.AwayWinProbability <- awayProb
                                existing.DrawWinProbability <- drawProb
                                existing.FetchedAt <- now
                                existing.Source <- source
                                existing.SourceSeason <- verifiedSeason
                                existing.ApplicableStart <- Nullable espnEvent.Date
                                existing.HomeTeamAbbreviation <- homeComp.Team.Abbreviation
                                existing.AwayTeamAbbreviation <- awayComp.Team.Abbreviation
                            else
                                let newPregameOdds =
                                    PregameOdds(
                                        GameId = gameId,
                                        HomeMoneyline = Nullable homeMoneyline,
                                        AwayMoneyline = Nullable awayMoneyline,
                                        DrawMoneyline = drawMoneyline,
                                        Spread = spread,
                                        OverUnder = overUnder,
                                        HomeWinProbability = homeProb,
                                        AwayWinProbability = awayProb,
                                        DrawWinProbability = drawProb,
                                        FetchedAt = now,
                                        Source = source,
                                        SourceSeason = verifiedSeason,
                                        ApplicableStart = Nullable espnEvent.Date,
                                        HomeTeamAbbreviation = homeComp.Team.Abbreviation,
                                        AwayTeamAbbreviation = awayComp.Team.Abbreviation
                                    )

                                db.PregameOdds.Add(newPregameOdds) |> ignore

                            updatedCount <- updatedCount + 1
                            if drawProb.HasValue then
                                logger.LogDebug(
                                    "Updated 3-way pregame odds for {Game}: Home {HomeML} ({HomeProb:P1}), Draw {DrawML} ({DrawProb:P1}), Away {AwayML} ({AwayProb:P1})",
                                    espnEvent.ShortName, homeMoneyline, homeProb, drawMoneyline.Value, drawProb.Value, awayMoneyline, awayProb)
                            else
                                logger.LogDebug(
                                    "Updated pregame odds for {Game}: Home {HomeML} ({HomeProb:P1}), Away {AwayML}",
                                    espnEvent.ShortName, homeMoneyline, homeProb, awayMoneyline)

            let! _ = db.SaveChangesAsync(ct)
            logger.LogInformation("Updated pregame odds for {Count} {Sport} games", updatedCount, sport)

            return updatedCount
        }
