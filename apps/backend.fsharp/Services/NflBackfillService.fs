namespace backend.Services.Backfill

open System
open System.Diagnostics
open System.Linq
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open backend.Data
open backend.Models
open backend.Services
open backend.Services.Espn
open backend.Services.Kalshi
open Microsoft.EntityFrameworkCore
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging

type NflBackfillService
    (httpClient: HttpClient,
     logger: ILogger<NflBackfillService>,
     serviceProvider: IServiceProvider,
     kalshiService: KalshiOddsService) =

    static let regularSeasonWeeks = 18
    static let postseasonWeeks = 5

    static let mapStatus (espnState: string) =
        match espnState with
        | "pre" -> "scheduled"
        | "in" -> "in_progress"
        | "post" -> "final"
        | _ -> "unknown"

    static let getPostseasonWeekLabel (weekNumber: int) =
        match weekNumber with
        | 1 -> "Wild Card"
        | 2 -> "Divisional"
        | 3 -> "Conference Championships"
        | 4 -> "Pro Bowl"
        | 5 -> "Super Bowl"
        | _ -> $"Postseason Week {weekNumber}"

    static let getWeekLabel (seasonType: int) (weekNumber: int) =
        match seasonType with
        | 1 -> $"Preseason Week {weekNumber}"
        | 2 -> $"Week {weekNumber}"
        | 3 -> getPostseasonWeekLabel weekNumber
        | _ -> $"Week {weekNumber}"

    static let probabilityToMoneyline (probability: float) =
        if probability <= 0.01 then 10000
        elif probability >= 0.99 then -10000
        elif probability >= 0.5 then
            int (Math.Round(-100.0 * probability / (1.0 - probability)))
        else
            int (Math.Round(100.0 * (1.0 - probability) / probability))

    let mutable coreOddsFailureSent = false

    member private _.FetchCoreApiOddsAsync(eventId: string, ct: CancellationToken) : Task<struct (int * int) voption> =
        task {
            let url =
                $"https://sports.core.api.espn.com/v2/sports/football/leagues/nfl/events/{eventId}/competitions/{eventId}/odds"

            try
                let! response = httpClient.GetStringAsync(url, ct)
                let oddsResponse = JsonSerializer.Deserialize<EspnCoreOddsResponse>(response)

                if isNull (box oddsResponse) || isNull oddsResponse.Items || oddsResponse.Items.Count = 0 then
                    return ValueNone
                else
                    let item = oddsResponse.Items.[0]

                    let mutable homeML: Nullable<int> = Nullable()
                    let mutable awayML: Nullable<int> = Nullable()

                    if
                        not (isNull (box item.HomeTeamOdds))
                        && not (isNull (box item.HomeTeamOdds.Close))
                        && not (isNull (box item.HomeTeamOdds.Close.MoneyLine))
                        && not (isNull item.HomeTeamOdds.Close.MoneyLine.American)
                    then
                        homeML <- Nullable(OddsConverter.ParseMoneyline(item.HomeTeamOdds.Close.MoneyLine.American))
                    elif not (isNull (box item.HomeTeamOdds)) && item.HomeTeamOdds.MoneyLine.HasValue then
                        homeML <- item.HomeTeamOdds.MoneyLine

                    if
                        not (isNull (box item.AwayTeamOdds))
                        && not (isNull (box item.AwayTeamOdds.Close))
                        && not (isNull (box item.AwayTeamOdds.Close.MoneyLine))
                        && not (isNull item.AwayTeamOdds.Close.MoneyLine.American)
                    then
                        awayML <- Nullable(OddsConverter.ParseMoneyline(item.AwayTeamOdds.Close.MoneyLine.American))
                    elif not (isNull (box item.AwayTeamOdds)) && item.AwayTeamOdds.MoneyLine.HasValue then
                        awayML <- item.AwayTeamOdds.MoneyLine

                    if homeML.HasValue && awayML.HasValue && homeML.Value <> 0 && awayML.Value <> 0 then
                        return ValueSome(struct (homeML.Value, awayML.Value))
                    else
                        return ValueNone
            with
            | :? OperationCanceledException when ct.IsCancellationRequested -> return raise (OperationCanceledException(ct))
            | ex ->
                let send = ProviderFailure.Is ex && not coreOddsFailureSent
                if send then coreOddsFailureSent <- true
                logger.Log((if send then LogLevel.Error else LogLevel.Warning), ex, "Failed to fetch ESPN Core API odds for event {EventId}", eventId)
                return ValueNone
        }

    member private this.TryFetchAndStorePregameOddsAsync
        (gameId: string,
         competition: EspnCompetition,
         db: ExcitementDbContext,
         ct: CancellationToken)
        : Task<bool> =
        task {
            try
                let! existing = db.PregameOdds.FindAsync([| gameId :> obj |], ct)

                if not (isNull existing) then
                    return false
                else
                    let odds =
                        if not (isNull competition.Odds) && competition.Odds.Count > 0 then
                            Some competition.Odds.[0]
                        else
                            None

                    let mutable homeMoneyline: Nullable<int> = Nullable()
                    let mutable awayMoneyline: Nullable<int> = Nullable()

                    match odds with
                    | Some o when
                        not (isNull (box o.Moneyline))
                        && not (isNull (box o.Moneyline.Home))
                        && not (isNull (box o.Moneyline.Home.Close))
                        && not (isNull o.Moneyline.Home.Close.Odds)
                        && not (isNull (box o.Moneyline.Away))
                        && not (isNull (box o.Moneyline.Away.Close))
                        && not (isNull o.Moneyline.Away.Close.Odds)
                        ->
                        homeMoneyline <- Nullable(OddsConverter.ParseMoneyline(o.Moneyline.Home.Close.Odds))
                        awayMoneyline <- Nullable(OddsConverter.ParseMoneyline(o.Moneyline.Away.Close.Odds))
                    | _ -> ()

                    if
                        not homeMoneyline.HasValue
                        || not awayMoneyline.HasValue
                        || homeMoneyline.Value = 0
                        || awayMoneyline.Value = 0
                    then
                        let! coreOdds = this.FetchCoreApiOddsAsync(gameId, ct)

                        match coreOdds with
                        | ValueSome(struct (hml, aml)) ->
                            homeMoneyline <- Nullable hml
                            awayMoneyline <- Nullable aml
                        | ValueNone -> ()

                    if
                        not homeMoneyline.HasValue
                        || not awayMoneyline.HasValue
                        || homeMoneyline.Value = 0
                        || awayMoneyline.Value = 0
                    then
                        logger.LogDebug("No pregame odds available for game {GameId}", gameId)
                        return false
                    else
                        let struct (homeProb, awayProb) =
                            OddsConverter.ToNormalizedProbabilities(homeMoneyline.Value, awayMoneyline.Value)

                        let pregameOdds =
                            PregameOdds(
                                GameId = gameId,
                                HomeMoneyline = Nullable homeMoneyline.Value,
                                AwayMoneyline = Nullable awayMoneyline.Value,
                                HomeWinProbability = homeProb,
                                AwayWinProbability = awayProb,
                                FetchedAt = DateTime.UtcNow,
                                Source = "espn_backfill"
                            )

                        db.PregameOdds.Add(pregameOdds) |> ignore

                        logger.LogDebug(
                            "Created pregame odds for game {GameId}: Home {Home}, Away {Away}",
                            gameId,
                            homeMoneyline,
                            awayMoneyline
                        )

                        return true
            with
            | :? OperationCanceledException when ct.IsCancellationRequested -> return raise (OperationCanceledException(ct))
            | ex ->
                logger.Log((if DatabaseFailure.Is ex then LogLevel.Error else LogLevel.Warning), ex, "Failed to fetch pregame odds for game {GameId}", gameId)
                return false
        }

    member private _.TryFetchAndStoreKalshiHistoricalOddsAsync
        (gameId: string,
         homeTeamAbbr: string,
         awayTeamAbbr: string,
         gameDate: DateTime,
         candlestickInterval: int,
         db: ExcitementDbContext,
         ct: CancellationToken)
        : Task<int> =
        task {
            try
                let! existingCount =
                    db.OddsSnapshots
                        .Where(fun (o: OddsSnapshot) -> o.GameId = gameId && o.Source = "kalshi_backfill")
                        .CountAsync(ct)

                if existingCount > 0 then
                    logger.LogDebug(
                        "Skipping Kalshi backfill for game {GameId} - already has {Count} snapshots",
                        gameId,
                        existingCount
                    )

                    return 0
                else
                    let! historicalOdds =
                        kalshiService.GetHistoricalOddsAsync(
                            "nfl",
                            homeTeamAbbr,
                            awayTeamAbbr,
                            gameDate,
                            candlestickInterval,
                            ct
                        )

                    if historicalOdds.Count = 0 then
                        logger.LogDebug("No Kalshi historical data available for game {GameId}", gameId)
                        return 0
                    else
                        let snapshots =
                            historicalOdds
                            |> Seq.map (fun odds ->
                                OddsSnapshot(
                                    GameId = gameId,
                                    Timestamp = odds.Timestamp,
                                    HomeWinProbability = odds.HomeWinProbability,
                                    AwayWinProbability = odds.AwayWinProbability,
                                    HomeMoneyline = probabilityToMoneyline odds.HomeWinProbability,
                                    AwayMoneyline = probabilityToMoneyline odds.AwayWinProbability,
                                    Source = "kalshi_backfill"
                                ))
                            |> Seq.toList

                        db.OddsSnapshots.AddRange(snapshots)

                        logger.LogInformation(
                            "Created {Count} Kalshi odds snapshots for game {GameId}",
                            snapshots.Length,
                            gameId
                        )

                        return snapshots.Length
            with
            | :? OperationCanceledException when ct.IsCancellationRequested -> return raise (OperationCanceledException(ct))
            | ex ->
                logger.Log((if DatabaseFailure.Is ex then LogLevel.Error else LogLevel.Warning), ex, "Failed to fetch Kalshi historical odds for game {GameId}", gameId)
                return 0
        }

    member private this.ProcessGameAsync
        (espnEvent: EspnEvent,
         seasonYear: int,
         seasonType: int,
         weekNumber: int,
         options: BackfillOptions,
         result: BackfillResult,
         db: ExcitementDbContext,
         ct: CancellationToken)
        : Task =
        task {
            if isNull espnEvent.Competitions || espnEvent.Competitions.Count = 0 then
                ()
            else
                let competition = espnEvent.Competitions.[0]

                let homeCompetitor =
                    competition.Competitors
                    |> Seq.tryFind (fun c -> c.HomeAway = "home")

                let awayCompetitor =
                    competition.Competitors
                    |> Seq.tryFind (fun c -> c.HomeAway = "away")

                match homeCompetitor, awayCompetitor with
                | None, _ | _, None -> ()
                | Some home, Some away ->
                    let! existingGame = db.Games.FindAsync([| espnEvent.Id :> obj |], ct)
                    let weekLabel = getWeekLabel seasonType weekNumber

                    if not (isNull existingGame) then
                        if options.SkipExisting then
                            result.GamesSkipped <- result.GamesSkipped + 1

                            logger.LogDebug(
                                "Skipping existing game {GameId}: {Away} @ {Home}",
                                espnEvent.Id,
                                away.Team.Abbreviation,
                                home.Team.Abbreviation
                            )

                            if
                                not existingGame.WeekNumber.HasValue
                                || not existingGame.SeasonType.HasValue
                            then
                                existingGame.WeekNumber <- Nullable weekNumber
                                existingGame.SeasonType <- Nullable seasonType
                                existingGame.WeekLabel <- weekLabel
                                existingGame.SeasonYear <- Nullable seasonYear
                                let! _ = db.SaveChangesAsync(ct)
                                ()

                            if options.BackfillKalshiOdds && not options.DryRun then
                                let! kalshiSnapshots =
                                    this.TryFetchAndStoreKalshiHistoricalOddsAsync(
                                        espnEvent.Id,
                                        home.Team.Abbreviation,
                                        away.Team.Abbreviation,
                                        espnEvent.Date,
                                        options.KalshiCandlestickInterval,
                                        db,
                                        ct
                                    )

                                if kalshiSnapshots > 0 then
                                    result.KalshiOddsSnapshotsCreated <-
                                        result.KalshiOddsSnapshotsCreated + kalshiSnapshots

                                    let! _ = db.SaveChangesAsync(ct)
                                    ()
                        else
                            existingGame.HomeTeam <- home.Team.DisplayName
                            existingGame.AwayTeam <- away.Team.DisplayName
                            existingGame.HomeTeamAbbreviation <- home.Team.Abbreviation
                            existingGame.AwayTeamAbbreviation <- away.Team.Abbreviation
                            existingGame.Status <- mapStatus espnEvent.Status.Type.State

                            existingGame.HomeScore <-
                                match Int32.TryParse(home.Score) with
                                | true, v -> Nullable v
                                | _ -> Nullable()

                            existingGame.AwayScore <-
                                match Int32.TryParse(away.Score) with
                                | true, v -> Nullable v
                                | _ -> Nullable()

                            existingGame.ScheduledStart <- espnEvent.Date
                            existingGame.SeasonYear <- Nullable seasonYear
                            existingGame.SeasonType <- Nullable seasonType
                            existingGame.WeekNumber <- Nullable weekNumber
                            existingGame.WeekLabel <- weekLabel

                            result.GamesUpdated <- result.GamesUpdated + 1

                            logger.LogDebug(
                                "Updated game {GameId}: {Away} @ {Home}",
                                espnEvent.Id,
                                away.Team.Abbreviation,
                                home.Team.Abbreviation
                            )

                            if not options.DryRun then
                                let! oddsCreated =
                                    this.TryFetchAndStorePregameOddsAsync(espnEvent.Id, competition, db, ct)

                                if oddsCreated then
                                    result.PregameOddsCreated <- result.PregameOddsCreated + 1

                            if options.BackfillKalshiOdds && not options.DryRun then
                                let! kalshiSnapshots =
                                    this.TryFetchAndStoreKalshiHistoricalOddsAsync(
                                        espnEvent.Id,
                                        home.Team.Abbreviation,
                                        away.Team.Abbreviation,
                                        espnEvent.Date,
                                        options.KalshiCandlestickInterval,
                                        db,
                                        ct
                                    )

                                result.KalshiOddsSnapshotsCreated <-
                                    result.KalshiOddsSnapshotsCreated + kalshiSnapshots
                    else
                        let homeScore =
                            match Int32.TryParse(home.Score) with
                            | true, v -> Nullable v
                            | _ -> Nullable()

                        let awayScore =
                            match Int32.TryParse(away.Score) with
                            | true, v -> Nullable v
                            | _ -> Nullable()

                        let game =
                            Game(
                                Id = espnEvent.Id,
                                Sport = "nfl",
                                HomeTeam = home.Team.DisplayName,
                                AwayTeam = away.Team.DisplayName,
                                HomeTeamAbbreviation = home.Team.Abbreviation,
                                AwayTeamAbbreviation = away.Team.Abbreviation,
                                ScheduledStart = espnEvent.Date,
                                Status = mapStatus espnEvent.Status.Type.State,
                                HomeScore = homeScore,
                                AwayScore = awayScore,
                                SeasonYear = Nullable seasonYear,
                                SeasonType = Nullable seasonType,
                                WeekNumber = Nullable weekNumber,
                                WeekLabel = weekLabel
                            )

                        if not options.DryRun then
                            db.Games.Add(game) |> ignore

                        result.GamesCreated <- result.GamesCreated + 1

                        logger.LogInformation(
                            "Created game {GameId}: {Away} @ {Home} ({Date})",
                            espnEvent.Id,
                            away.Team.Abbreviation,
                            home.Team.Abbreviation,
                            espnEvent.Date
                        )

                        if not options.DryRun then
                            let! oddsCreated =
                                this.TryFetchAndStorePregameOddsAsync(espnEvent.Id, competition, db, ct)

                            if oddsCreated then
                                result.PregameOddsCreated <- result.PregameOddsCreated + 1

                        if options.BackfillKalshiOdds && not options.DryRun then
                            let! kalshiSnapshots =
                                this.TryFetchAndStoreKalshiHistoricalOddsAsync(
                                    espnEvent.Id,
                                    home.Team.Abbreviation,
                                    away.Team.Abbreviation,
                                    espnEvent.Date,
                                    options.KalshiCandlestickInterval,
                                    db,
                                    ct
                                )

                            result.KalshiOddsSnapshotsCreated <-
                                result.KalshiOddsSnapshotsCreated + kalshiSnapshots
        }
        :> Task

    member private this.BackfillWeekAsync
        (seasonYear: int,
         seasonType: int,
         weekNumber: int,
         options: BackfillOptions,
         result: BackfillResult,
         ct: CancellationToken)
        : Task =
        task {
            let! scoreboard = this.FetchScoreboardByWeekAsync(seasonYear, seasonType, weekNumber, ct)

            if isNull (box scoreboard) || isNull scoreboard.Events then
                logger.LogWarning(
                    "No scoreboard data for {Year} ST{Type} Week {Week}",
                    seasonYear,
                    seasonType,
                    weekNumber
                )
            else
                logger.LogInformation(
                    "Processing {Count} games for Week {Week}",
                    scoreboard.Events.Count,
                    weekNumber
                )

                use scope = serviceProvider.CreateScope()
                let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()

                for espnEvent in scoreboard.Events do
                    try
                        do!
                            this.ProcessGameAsync(
                                espnEvent,
                                seasonYear,
                                seasonType,
                                weekNumber,
                                options,
                                result,
                                db,
                                ct
                            )
                    with
                    | :? OperationCanceledException when ct.IsCancellationRequested -> raise (OperationCanceledException(ct))
                    | ex ->
                        logger.Log((if DatabaseFailure.Is ex then LogLevel.Error else LogLevel.Warning), ex, "Error processing game {GameId}", espnEvent.Id)
                        result.Errors.Add($"Game {espnEvent.Id}: {ex.Message}")

                if not options.DryRun then
                    let! _ = db.SaveChangesAsync(ct)
                    ()
        }
        :> Task

    member private this.BackfillSeasonTypeAsync
        (seasonYear: int,
         seasonType: int,
         maxWeeks: int,
         options: BackfillOptions,
         result: BackfillResult,
         ct: CancellationToken)
        : Task =
        task {
            let seasonTypeName =
                if seasonType = 2 then "Regular Season" else "Postseason"

            let startWeek =
                if options.StartWeek.HasValue then options.StartWeek.Value else 1

            let endWeek =
                if options.EndWeek.HasValue then options.EndWeek.Value else maxWeeks

            logger.LogInformation(
                "Processing {SeasonType} weeks {Start}-{End}",
                seasonTypeName,
                startWeek,
                endWeek
            )

            let mutable week = startWeek

            while week <= Math.Min(endWeek, maxWeeks) do
                try
                    do! this.BackfillWeekAsync(seasonYear, seasonType, week, options, result, ct)
                    do! Task.Delay(500, ct)
                with
                | :? OperationCanceledException when ct.IsCancellationRequested -> raise (OperationCanceledException(ct))
                | ex ->
                    logger.Log((if DatabaseFailure.Is ex then LogLevel.Error else LogLevel.Warning), ex, "Error processing {SeasonType} week {Week}", seasonTypeName, week)
                    result.Errors.Add($"{seasonTypeName} Week {week}: {ex.Message}")

                week <- week + 1
        }
        :> Task

    member _.FetchScoreboardByWeekAsync
        (seasonYear: int,
         seasonType: int,
         weekNumber: int,
         ?ct: CancellationToken)
        : Task<EspnScoreboardResponse> =
        let ct = defaultArg ct CancellationToken.None

        task {
            let url =
                EspnSiteApi.url $"/apis/site/v2/sports/football/nfl/scoreboard?seasontype={seasonType}&week={weekNumber}&dates={seasonYear}"

            try
                let! response = httpClient.GetStringAsync(url, ct)
                return JsonSerializer.Deserialize<EspnScoreboardResponse>(response)
            with
            | :? OperationCanceledException when ct.IsCancellationRequested -> return raise (OperationCanceledException(ct))
            | ex ->
                logger.Log(
                    (if ProviderFailure.Is ex then LogLevel.Error else LogLevel.Warning),
                    ex,
                    "Failed to fetch ESPN scoreboard for {Year} ST{Type} Week {Week}",
                    seasonYear,
                    seasonType,
                    weekNumber
                )

                return Unchecked.defaultof<EspnScoreboardResponse>
        }

    member this.BackfillSeasonAsync
        (seasonYear: int, options: BackfillOptions, ?ct: CancellationToken)
        : Task<BackfillResult> =
        let ct = defaultArg ct CancellationToken.None

        task {
            let stopwatch = Stopwatch.StartNew()
            let result = BackfillResult.Create()

            logger.LogInformation(
                "Starting NFL {Season} backfill (DryRun: {DryRun})",
                seasonYear,
                options.DryRun
            )

            try
                if not options.SeasonType.HasValue || options.SeasonType.Value = 2 then
                    do!
                        this.BackfillSeasonTypeAsync(
                            seasonYear,
                            2,
                            regularSeasonWeeks,
                            options,
                            result,
                            ct
                        )

                if not options.SeasonType.HasValue || options.SeasonType.Value = 3 then
                    do!
                        this.BackfillSeasonTypeAsync(
                            seasonYear,
                            3,
                            postseasonWeeks,
                            options,
                            result,
                            ct
                        )

                if options.CalculateElo && not options.DryRun then
                    use scope = serviceProvider.CreateScope()
                    let eloService = scope.ServiceProvider.GetRequiredService<RetrospectiveEloService>()
                    let! ratingsCreated = eloService.CalculateSeasonEloAsync(seasonYear, ct)
                    result.TeamRatingsCreated <- ratingsCreated
            with
            | :? OperationCanceledException when ct.IsCancellationRequested -> raise (OperationCanceledException(ct))
            | ex ->
                logger.Log((if DatabaseFailure.Is ex then LogLevel.Error else LogLevel.Warning), ex, "Backfill failed with error")
                result.Errors.Add($"Fatal error: {ex.Message}")

            stopwatch.Stop()
            result.Duration <- stopwatch.Elapsed

            logger.LogInformation(
                "NFL {Season} backfill complete: {Created} created, {Updated} updated, {Skipped} skipped, {Odds} pregame odds, {Ratings} ratings in {Duration:g}",
                seasonYear,
                result.GamesCreated,
                result.GamesUpdated,
                result.GamesSkipped,
                result.PregameOddsCreated,
                result.TeamRatingsCreated,
                result.Duration
            )

            return result
        }
