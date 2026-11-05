namespace backend.Services.Espn

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Linq
open Microsoft.EntityFrameworkCore
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open backend.Data
open backend.Models
open backend.Services
open backend.Services.Kalshi
open backend.GraphQL.Types
open Backend.FSharp

module WorldCupStatus =
    let isAdvancePhase (statusName: string) =
        match statusName with
        | "STATUS_OVERTIME"
        | "STATUS_EXTRA_TIME"
        | "STATUS_SHOOTOUT"
        | "STATUS_PENALTY_SHOOTOUT" -> true
        | _ -> false

type EspnOddsService
    (httpClient: Net.Http.HttpClient,
     logger: ILogger<EspnOddsService>,
     serviceProvider: IServiceProvider,
     gameUpdateService: IGameUpdateService,
     kalshiOddsService: KalshiOddsService) =

    let registeredTime = if isNull serviceProvider then null else serviceProvider.GetService<TimeProvider>()
    let timeProvider = if isNull registeredTime then TimeProvider.System else registeredTime

    static let eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York")

    static let sportEndpoints =
        let d = Dictionary<string, struct (string * string)>(StringComparer.OrdinalIgnoreCase)
        d.["nba"] <- struct ("basketball/nba", "nba")
        d.["nfl"] <- struct ("football/nfl", "nfl")
        d.["nhl"] <- struct ("hockey/nhl", "nhl")
        d.["mlb"] <- struct ("baseball/mlb", "mlb")
        d.["world_cup"] <- struct ("soccer/fifa.world", "fifa.world")
        d

    static let mapStatus (espnState: string) =
        match espnState with
        | "pre" -> "scheduled"
        | "in" -> "in_progress"
        | "post" -> "final"
        | _ -> "unknown"

    static let roundFromTypeAbbreviation (abbr: string) =
        if isNull abbr then None
        else
            match abbr.ToUpper() with
            | "RD16" -> Some 1
            | "RD8" | "QTR" -> Some 2
            | "RD4" | "SEMI" -> Some 3
            | "RD2" | "FNL" -> Some 4
            | _ -> None

    static let roundFromNotesHeadline (headline: string) =
        if isNull headline then None
        else
            let h = headline.ToLower()
            if h.Contains("1st round") then Some 1
            elif h.Contains("semifinal") || h.Contains("2nd round") then Some 2
            elif h.Contains("conference final") then Some 3
            elif h.Contains("stanley cup final") || h.Contains("nba final") || h.Contains("world series") || h.Contains("super bowl") then Some 4
            else None

    static let getPostseasonWeekLabel (sport: string) (weekNumber: int) =
        match sport.ToLower() with
        | "nfl" ->
            match weekNumber with
            | 1 -> "Wild Card"
            | 2 -> "Divisional"
            | 3 -> "Conference Championships"
            | 4 -> "Pro Bowl"
            | 5 -> "Super Bowl"
            | n -> $"Postseason Week {n}"
        | "nba" | "nhl" ->
            match weekNumber with
            | 1 -> "First Round"
            | 2 -> "Conference Semifinals"
            | 3 -> "Conference Finals"
            | 4 -> "Finals"
            | n -> $"Postseason Round {n}"
        | _ -> $"Postseason Week {weekNumber}"

    static let getWeekLabel (sport: string) (seasonType: Nullable<int>) (weekNumber: Nullable<int>) =
        if not weekNumber.HasValue then
            null
        else
            let wn = weekNumber.Value

            if not seasonType.HasValue then
                $"Week {wn}"
            else
                match seasonType.Value with
                | 1 -> $"Preseason Week {wn}"
                | 2 -> $"Week {wn}"
                | 3 -> getPostseasonWeekLabel sport wn
                | _ -> $"Week {wn}"

    member this.FetchScoreboardAsync(sport: string, ?date: string, ?ct: CancellationToken) : Task<EspnScoreboardResponse> =
        this.FetchScoreboardCoreAsync(sport, PlayoffSeriesCatalogue.Supports(sport.ToLower()), date, defaultArg ct CancellationToken.None)

    member private _.FetchScoreboardCoreAsync(sport: string, supportsSeries: bool, date: string option, ct: CancellationToken) : Task<EspnScoreboardResponse> =
        task {
            match sportEndpoints.TryGetValue(sport.ToLower()) with
            | false, _ ->
                logger.LogWarning("Unknown sport: {Sport}", sport)
                return Unchecked.defaultof<EspnScoreboardResponse>
            | true, struct (path, _) ->

            let mutable url = EspnSiteApi.url $"/apis/site/v2/sports/{path}/scoreboard"

            match date with
            | Some d -> url <- url + $"?dates={d}"
            | None -> ()

            try
                let! response = httpClient.GetStringAsync(url, ct)
                use document = JsonDocument.Parse(response)
                let scoreboard =
                    try JsonSerializer.Deserialize<EspnScoreboardResponse>(response)
                    with :? JsonException when supportsSeries ->
                        let root = document.RootElement
                        let readField (name: string) decode =
                            let mutable field = Unchecked.defaultof<JsonElement>
                            if root.TryGetProperty(name, &field) then
                                try decode (field.GetRawText()) with :? JsonException -> Unchecked.defaultof<_>
                            else Unchecked.defaultof<_>
                        let events =
                            root.GetProperty("events").EnumerateArray()
                            |> Seq.choose (fun item ->
                                try Some (JsonSerializer.Deserialize<EspnEvent>(item.GetRawText()))
                                with :? JsonException -> None)
                        { Events = ResizeArray(events)
                          Week = readField "week" (fun json -> JsonSerializer.Deserialize<EspnWeek>(json: string))
                          Season = readField "season" (fun json -> JsonSerializer.Deserialize<EspnSeason>(json: string))
                          Leagues = readField "leagues" (fun json -> JsonSerializer.Deserialize<ResizeArray<EspnLeague>>(json: string))
                          RawJson = root.Clone() }
                return { scoreboard with RawJson = document.RootElement.Clone() }
            with
            | :? OperationCanceledException -> logger.LogDebug("Operation cancelled: ESPN scoreboard fetch for {Sport} on {Date}", sport, (defaultArg date "today")); return Unchecked.defaultof<EspnScoreboardResponse>
            | ex ->
                logger.LogError(ex, "Failed to fetch ESPN scoreboard for {Sport} on {Date}", sport, (defaultArg date "today"))
                return Unchecked.defaultof<EspnScoreboardResponse>
        }

    member this.ScrapeAndStoreOddsAsync(sport: string, ?ct: CancellationToken) : Task<ResizeArray<OddsSnapshot>> =
        let ct = defaultArg ct CancellationToken.None

        task {
            let allSnapshots = ResizeArray<OddsSnapshot>()
            let supportsSeries = PlayoffSeriesCatalogue.Supports(sport.ToLower())

            let nowEastern = TimeZoneInfo.ConvertTimeFromUtc(timeProvider.GetUtcNow().UtcDateTime, eastern)
            let dates = ResizeArray<string>()
            let endOffset = if sport.Equals(SportExtensions.ToDbValue(Sport.WORLD_CUP), StringComparison.OrdinalIgnoreCase) then 1 else 7

            for i in -1 .. endOffset do
                dates.Add(nowEastern.AddDays(float i).ToString("yyyyMMdd"))

            for date in dates do
                let! snapshots = this.ScrapeAndStoreOddsForDateAsync(sport, supportsSeries, Some date, ct)
                allSnapshots.AddRange(snapshots)

            return allSnapshots
        }

    member private this.ScrapeAndStoreOddsForDateAsync(sport: string, supportsSeries: bool, date: string option, ct: CancellationToken) : Task<ResizeArray<OddsSnapshot>> =
        task {
            let seriesObservedAt = timeProvider.GetUtcNow().UtcDateTime
            let! scoreboard = this.FetchScoreboardCoreAsync(sport, supportsSeries, date, ct)

            if isNull (box scoreboard) then
                return ResizeArray<OddsSnapshot>()
            else

            let snapshots = ResizeArray<OddsSnapshot>()
            let espnEventIds = HashSet<string>(StringComparer.OrdinalIgnoreCase)
            let now = timeProvider.GetUtcNow().UtcDateTime

            use scope = serviceProvider.CreateScope()
            let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
            let sportLower = sport.ToLower()
            let isWorldCup = sportLower = SportExtensions.ToDbValue(Sport.WORLD_CUP)
            let publishGameIds = HashSet<string>()
            let completionObservations = ResizeArray<struct (MlbCompletionEvidence * bool)>()
            let seriesObservations = ResizeArray<PlayoffSeriesObservation>()
            let suppliedSeriesIds = HashSet<string>()
            if supportsSeries then
                let mutable rawEvents = Unchecked.defaultof<JsonElement>
                if scoreboard.RawJson.TryGetProperty("events", &rawEvents) && rawEvents.ValueKind = JsonValueKind.Array then
                    for rawEvent in rawEvents.EnumerateArray() do
                        let mutable rawId = Unchecked.defaultof<JsonElement>
                        if rawEvent.ValueKind = JsonValueKind.Object && rawEvent.TryGetProperty("id", &rawId)
                            && rawId.ValueKind = JsonValueKind.String then
                            espnEventIds.Add(rawId.GetString()) |> ignore
                            let mutable competitions = Unchecked.defaultof<JsonElement>
                            if rawEvent.TryGetProperty("competitions", &competitions) && competitions.ValueKind = JsonValueKind.Array then
                                for competition in competitions.EnumerateArray() do
                                    let mutable series = Unchecked.defaultof<JsonElement>
                                    if competition.ValueKind = JsonValueKind.Object && competition.TryGetProperty("series", &series) then
                                        suppliedSeriesIds.Add(rawId.GetString()) |> ignore

            for espnEvent in scoreboard.Events |> seqOfObj |> Seq.filter (fun e -> not (isNull (box e)) && not (String.IsNullOrWhiteSpace(e.Id))) do
                let! rawIdGame = db.Games.FindAsync([| espnEvent.Id :> obj |], ct)
                let gameId =
                    if isWorldCup && not (isNull rawIdGame) && rawIdGame.Sport <> sportLower then
                        $"worldcup-{espnEvent.Id}"
                    else
                        espnEvent.Id

                espnEventIds.Add(gameId) |> ignore

                let priorSeriesObservation = if isNull rawIdGame then null else PlayoffSeriesObservation.Parse(rawIdGame.SeriesObservationJson)
                let supersededSeriesObservation =
                    supportsSeries
                    && not (isNull rawIdGame) && rawIdGame.SeasonType = Nullable<int>(3)
                    && not (isNull priorSeriesObservation)
                    && (priorSeriesObservation.ObservedAt > seriesObservedAt
                        || (priorSeriesObservation.ObservedAt = seriesObservedAt
                            && (not (isNull priorSeriesObservation.Snapshot) || rawIdGame.Status = "canceled"
                                || (espnEvent.Competitions |> seqOfObj |> Seq.exists (fun c -> not (isNull (box c)) && c.Series.ValueKind <> JsonValueKind.Undefined)))))

                let conflictingSeriesSport =
                    supportsSeries
                    && not (isNull rawIdGame) && rawIdGame.Sport <> sportLower
                if conflictingSeriesSport then
                    seriesObservations.Add(PlayoffSeriesObservation(GameId = gameId, Source = "espn", ObservedAt = seriesObservedAt, Snapshot = null))

                let missingSeriesStatus =
                    supportsSeries
                    && (isNull (box espnEvent.Status) || isNull (box espnEvent.Status.Type))

                match supersededSeriesObservation || conflictingSeriesSport || missingSeriesStatus with
                | true -> ()
                | false ->

                let competition = espnEvent.Competitions |> seqOfObj |> Seq.tryHead |> Option.filter (fun c -> not (isNull (box c)))

                match competition with
                | None -> ()
                | Some competition ->

                let participants = competition.Competitors |> seqOfObj |> Seq.filter (fun c -> not (isNull (box c)) && not (isNull (box c.Team)))
                let homeCompetitor = participants |> Seq.tryFind (fun c -> c.HomeAway = "home")
                let awayCompetitor = participants |> Seq.tryFind (fun c -> c.HomeAway = "away")

                match homeCompetitor, awayCompetitor with
                | None, _ | _, None -> ()
                | Some homeComp, Some awayComp ->

                let eventSeason = espnEvent.Season
                let eventWeek = espnEvent.Week

                let seasonYear =
                    if not (isNull (box eventSeason)) && eventSeason.Year > 0 then
                        Nullable<int>(eventSeason.Year)
                    elif not (isNull (box scoreboard.Season)) && scoreboard.Season.Year > 0 then
                        Nullable<int>(scoreboard.Season.Year)
                    else
                        Nullable<int>()

                let seasonType = EspnSeasonMetadata.seasonType eventSeason scoreboard

                let weekNumber =
                    if not (isNull (box eventWeek)) && eventWeek.Number > 0 then
                        Nullable<int>(eventWeek.Number)
                    elif not (isNull (box scoreboard.Week)) && scoreboard.Week.Number > 0 then
                        Nullable<int>(scoreboard.Week.Number)
                    else
                        let typeAbbr =
                            if not (isNull (box competition)) && not (isNull (box competition.Type)) then
                                competition.Type.Abbreviation
                            else null
                        match roundFromTypeAbbreviation typeAbbr with
                        | Some round -> Nullable<int>(round)
                        | None ->
                            let headline =
                                competition.Notes
                                |> seqOfObj
                                |> Seq.tryHead
                                |> Option.filter (fun n -> not (isNull (box n)))
                                |> Option.map (fun n -> n.Headline)
                                |> Option.defaultValue null
                            match roundFromNotesHeadline headline with
                            | Some round -> Nullable<int>(round)
                            | None -> Nullable<int>()

                let weekLabel = getWeekLabel sport seasonType weekNumber
                let isWorldCupOvertime =
                    isWorldCup
                    && not (isNull (box espnEvent.Status.Type))
                    && WorldCupStatus.isAdvancePhase espnEvent.Status.Type.Name

                let! game =
                    if gameId = espnEvent.Id then
                        Task.FromResult(rawIdGame)
                    else
                        db.Games.FindAsync([| gameId :> obj |], ct).AsTask()

                let allowOrdinaryDiscovery =
                    isNull game || game.Status <> "final" || not (isNull game.EspnCompletionEvidenceJson)
                let broadcastNetworks = EspnBroadcasts.ofCompetition competition

                if isNull game then
                    let mutable homeScore = 0
                    let mutable awayScore = 0
                    let newGame =
                        Game(
                            Id = gameId,
                            Sport = sportLower,
                            HomeTeam = homeComp.Team.DisplayName,
                            AwayTeam = awayComp.Team.DisplayName,
                            HomeTeamAbbreviation = homeComp.Team.Abbreviation,
                            AwayTeamAbbreviation = awayComp.Team.Abbreviation,
                            HomeTeamId = homeComp.Id,
                            AwayTeamId = awayComp.Id,
                            ScheduledStart = espnEvent.Date,
                            Status = mapStatus espnEvent.Status.Type.State,
                            IsOvertime = isWorldCupOvertime,
                            HomeScore = (if Int32.TryParse(homeComp.Score, &homeScore) then Nullable<int>(homeScore) else Nullable<int>()),
                            AwayScore = (if Int32.TryParse(awayComp.Score, &awayScore) then Nullable<int>(awayScore) else Nullable<int>()),
                            SeasonYear = seasonYear,
                            SeasonType = seasonType,
                            WeekNumber = weekNumber,
                            WeekLabel = weekLabel,
                            BroadcastNetworks = (if broadcastNetworks.IsEmpty then null else List.toArray broadcastNetworks)
                        )

                    db.Games.Add(newGame) |> ignore
                else
                    game.Status <- mapStatus espnEvent.Status.Type.State
                    game.IsOvertime <- isWorldCupOvertime

                    let mutable homeScore = 0

                    game.HomeScore <-
                        if Int32.TryParse(homeComp.Score, &homeScore) then
                            Nullable<int>(homeScore)
                        else
                            Nullable<int>()

                    let mutable awayScore = 0

                    game.AwayScore <-
                        if Int32.TryParse(awayComp.Score, &awayScore) then
                            Nullable<int>(awayScore)
                        else
                            Nullable<int>()

                    let sameTeam (provider: EspnCompetitor) (id: string) (abbreviation: string) =
                        not (isNull (box provider.Team))
                        && not (String.IsNullOrWhiteSpace(provider.Id))
                        && (isNull provider.Team.Id || provider.Team.Id = provider.Id)
                        && (isNull id || id = provider.Id)
                        && backend.Scoring.ProHypePolicy.TeamKey(sportLower, provider.Team.Abbreviation)
                            = backend.Scoring.ProHypePolicy.TeamKey(sportLower, abbreviation)
                    let reversedVenue =
                        supportsSeries
                        && game.Sport = sportLower && competition.Id = gameId
                        && competition.Competitors.Count = 2 && homeComp.Id <> awayComp.Id
                        && sameTeam homeComp game.AwayTeamId game.AwayTeamAbbreviation
                        && sameTeam awayComp game.HomeTeamId game.HomeTeamAbbreviation
                    if reversedVenue then
                        let oldHome = game.HomeTeam
                        let oldHomeAbbreviation = game.HomeTeamAbbreviation
                        game.HomeTeam <- game.AwayTeam
                        game.HomeTeamAbbreviation <- game.AwayTeamAbbreviation
                        game.AwayTeam <- oldHome
                        game.AwayTeamAbbreviation <- oldHomeAbbreviation
                        game.HomeTeamId <- homeComp.Id
                        game.AwayTeamId <- awayComp.Id

                    let isPlaceholder (name: string) = name = "TBD" || name.Contains('/')
                    let isRealTeam (name: string) = not (isPlaceholder name)

                    if isPlaceholder game.HomeTeam && isRealTeam homeComp.Team.DisplayName then
                        game.HomeTeam <- homeComp.Team.DisplayName
                        game.HomeTeamAbbreviation <- homeComp.Team.Abbreviation
                        logger.LogInformation("Updated home team for {Game}: {Team}", espnEvent.ShortName, game.HomeTeam)

                    if isPlaceholder game.AwayTeam && isRealTeam awayComp.Team.DisplayName then
                        game.AwayTeam <- awayComp.Team.DisplayName
                        game.AwayTeamAbbreviation <- awayComp.Team.Abbreviation
                        logger.LogInformation("Updated away team for {Game}: {Team}", espnEvent.ShortName, game.AwayTeam)

                    if game.ScheduledStart <> espnEvent.Date then
                        logger.LogInformation(
                            "Updated scheduled start for {Game}: {OldTime} -> {NewTime}",
                            espnEvent.ShortName, game.ScheduledStart, espnEvent.Date)
                        game.ScheduledStart <- espnEvent.Date

                    if not game.SeasonYear.HasValue && seasonYear.HasValue then
                        game.SeasonYear <- seasonYear

                    if seasonType.HasValue then
                        game.SeasonType <- seasonType

                    if not game.WeekNumber.HasValue && weekNumber.HasValue then
                        game.WeekNumber <- weekNumber

                    if (isNull game.WeekLabel || game.WeekLabel = "") && not (isNull weekLabel) && weekLabel <> "" then
                        game.WeekLabel <- weekLabel

                    if not broadcastNetworks.IsEmpty then
                        game.BroadcastNetworks <- List.toArray broadcastNetworks

                if supportsSeries then
                    let observedGame = if isNull game then db.Games.Local |> Seq.find (fun g -> g.Id = gameId) else game
                    seriesObservations.Add(PlayoffSeriesCollector.Observe(observedGame, scoreboard.RawJson, seriesObservedAt))

                if sportLower = "mlb" then
                    let completionStatus (status: EspnStatus) =
                        if isNull (box status) || isNull (box status.Type) then
                            EspnCompletionStatus(null, null, Nullable<bool>(), null, null, null)
                        else
                            EspnCompletionStatus(status.Type.Name, status.Type.State, status.Type.Completed, status.Type.Description, status.Detail, status.ShortDetail)
                    let evidence =
                        MlbCompletionEvidence(
                            espnEvent.Id,
                            competition.Id,
                            completionStatus espnEvent.Status,
                            completionStatus competition.Status,
                            timeProvider.GetUtcNow().UtcDateTime)
                    completionObservations.Add(struct (evidence, allowOrdinaryDiscovery))

                let gameState = espnEvent.Status.Type.State
                if gameState <> "pre" then
                    publishGameIds.Add(gameId) |> ignore

                if gameState <> "post" then
                    if not (gameState = "pre" && espnEvent.Date > now.AddMinutes(5.0)) then
                        let mutable homeMoneyline = 0
                        let mutable awayMoneyline = 0
                        let mutable drawMoneyline = Nullable<int>()
                        let mutable homeProb = 0.0
                        let mutable awayProb = 0.0
                        let mutable drawProb = Nullable<float>()
                        let mutable spread = Nullable<float>()
                        let mutable overUnder = Nullable<float>()
                        let mutable oddsSource = ""
                        let mutable skipSnapshot = false

                        if isWorldCup then
                            let mutable foundWorldCupOdds = false

                            if gameState = "in" && isWorldCupOvertime then
                                let! kalshiOdds =
                                    kalshiOddsService.GetWorldCupAdvanceLiveOddsAsync(
                                        homeComp.Team.Abbreviation,
                                        awayComp.Team.Abbreviation,
                                        espnEvent.Date,
                                        ct)

                                if not (isNull (box kalshiOdds)) then
                                    homeMoneyline <- kalshiOdds.HomeMoneyline
                                    awayMoneyline <- kalshiOdds.AwayMoneyline
                                    oddsSource <- if kalshiOdds.Source = "Kalshi" then "Kalshi (to advance)" else kalshiOdds.Source
                                    homeProb <- kalshiOdds.HomeWinProbability
                                    awayProb <- kalshiOdds.AwayWinProbability
                                    foundWorldCupOdds <- true
                                    logger.LogInformation(
                                        "Using Kalshi World Cup advance odds for {Game}: Home {HomeProb:P1}, Away {AwayProb:P1}",
                                        espnEvent.ShortName,
                                        kalshiOdds.HomeWinProbability,
                                        kalshiOdds.AwayWinProbability)
                            elif gameState = "in" then
                                let! kalshiOdds =
                                    kalshiOddsService.GetWorldCupThreeWayLiveOddsAsync(
                                        homeComp.Team.Abbreviation,
                                        awayComp.Team.Abbreviation,
                                        espnEvent.Date,
                                        ct)

                                if not (isNull (box kalshiOdds)) then
                                    homeMoneyline <- kalshiOdds.HomeMoneyline
                                    drawMoneyline <- Nullable<int>(kalshiOdds.DrawMoneyline)
                                    awayMoneyline <- kalshiOdds.AwayMoneyline
                                    oddsSource <- kalshiOdds.Source
                                    homeProb <- kalshiOdds.HomeWinProbability
                                    drawProb <- Nullable<float>(kalshiOdds.DrawWinProbability)
                                    awayProb <- kalshiOdds.AwayWinProbability
                                    foundWorldCupOdds <- true
                                    logger.LogInformation(
                                        "Using Kalshi 3-way World Cup odds for {Game}: Home {HomeProb:P1}, Draw {DrawProb:P1}, Away {AwayProb:P1}",
                                        espnEvent.ShortName,
                                        kalshiOdds.HomeWinProbability,
                                        kalshiOdds.DrawWinProbability,
                                        kalshiOdds.AwayWinProbability)

                            if not foundWorldCupOdds && not isWorldCupOvertime then
                                match EspnOddsParsing.tryFindCompleteThreeWayOdds competition.Odds with
                                | Some threeWay ->
                                    homeMoneyline <- threeWay.HomeMoneyline
                                    drawMoneyline <- Nullable<int>(threeWay.DrawMoneyline)
                                    awayMoneyline <- threeWay.AwayMoneyline
                                    spread <- threeWay.Spread
                                    overUnder <- threeWay.OverUnder
                                    oddsSource <- if String.IsNullOrEmpty threeWay.ProviderName then "espn" else threeWay.ProviderName
                                    let struct (hp, dp, ap) = OddsConverter.ToNormalizedThreeWayProbabilities(homeMoneyline, drawMoneyline.Value, awayMoneyline)
                                    homeProb <- hp
                                    drawProb <- Nullable<float>(dp)
                                    awayProb <- ap
                                    foundWorldCupOdds <- true
                                | None -> ()

                            if not foundWorldCupOdds then
                                let marketType = if isWorldCupOvertime then "advance" else "3-way"
                                logger.LogDebug("No complete {MarketType} odds for World Cup game {Game}, skipping odds snapshot", marketType, espnEvent.ShortName)
                                skipSnapshot <- true
                        else
                            let! kalshiOdds =
                                kalshiOddsService.GetLiveOddsAsync(
                                    sport,
                                    homeComp.Team.Abbreviation,
                                    awayComp.Team.Abbreviation,
                                    espnEvent.Date,
                                    ct)

                            if not (isNull (box kalshiOdds)) then
                                homeMoneyline <- kalshiOdds.HomeMoneyline
                                awayMoneyline <- kalshiOdds.AwayMoneyline
                                oddsSource <- kalshiOdds.Source
                                homeProb <- kalshiOdds.HomeWinProbability
                                awayProb <- kalshiOdds.AwayWinProbability
                                logger.LogInformation(
                                    "Using Kalshi odds for {Game}: Home {Home}, Away {Away} (prob: {HomeProb:P1}/{AwayProb:P1})",
                                    espnEvent.ShortName, homeMoneyline, awayMoneyline,
                                    kalshiOdds.HomeWinProbability, kalshiOdds.AwayWinProbability)
                            else
                                if gameState = "in" then
                                    logger.LogWarning(
                                        "No Kalshi market found for in-progress game {Game} ({GameId}), skipping odds snapshot",
                                        espnEvent.ShortName, gameId)
                                else
                                    logger.LogDebug(
                                        "No Kalshi odds for {Game}, skipping odds snapshot",
                                        espnEvent.ShortName)
                                skipSnapshot <- true

                        if not skipSnapshot then

                            let mutable hs = 0
                            let mutable aws = 0

                            let snapshot =
                                OddsSnapshot(
                                    GameId = gameId,
                                    Timestamp = now,
                                    HomeMoneyline = homeMoneyline,
                                    AwayMoneyline = awayMoneyline,
                                    DrawMoneyline = drawMoneyline,
                                    Spread = spread,
                                    OverUnder = overUnder,
                                    HomeWinProbability = homeProb,
                                    AwayWinProbability = awayProb,
                                    DrawWinProbability = drawProb,
                                    GameClock = espnEvent.Status.DisplayClock,
                                    Period = Nullable<int>(espnEvent.Status.Period),
                                    HomeScore = (if Int32.TryParse(homeComp.Score, &hs) then Nullable<int>(hs) else Nullable<int>()),
                                    AwayScore = (if Int32.TryParse(awayComp.Score, &aws) then Nullable<int>(aws) else Nullable<int>()),
                                    Source = oddsSource
                                )

                            db.OddsSnapshots.Add(snapshot) |> ignore
                            snapshots.Add(snapshot)
                            publishGameIds.Add(gameId) |> ignore

                            if drawProb.HasValue then
                                logger.LogDebug(
                                    "Scraped 3-way odds for {Game}: Home {HomeProb:P1}, Draw {DrawProb:P1}, Away {AwayProb:P1}",
                                    espnEvent.ShortName, homeProb, drawProb.Value, awayProb)
                            else
                                logger.LogDebug(
                                    "Scraped odds for {Game}: Home {HomeProb:P1}, Away {AwayProb:P1}",
                                    espnEvent.ShortName, homeProb, awayProb)
                    else
                        logger.LogDebug(
                            "Skipping odds for {Game} - game starts at {Start}, more than 5 minutes away",
                            espnEvent.ShortName, espnEvent.Date)
                else
                    logger.LogDebug("Skipping odds for {Game} - game is final", espnEvent.ShortName)

            if supportsSeries then
                let observedIds = seriesObservations |> Seq.map (fun o -> o.GameId) |> HashSet<string>
                for gameId in espnEventIds do
                    if not (observedIds.Contains(gameId)) then
                        let! knownGame = db.Games.FindAsync([| gameId :> obj |], ct)
                        if not (isNull knownGame) then
                            seriesObservations.Add(PlayoffSeriesCollector.Observe(knownGame, scoreboard.RawJson, seriesObservedAt))

            match date with
            | Some dateStr when dateStr.Length = 8 ->
                let dateVal = DateTime.ParseExact(dateStr, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)
                let easternDate = DateTime.SpecifyKind(dateVal, DateTimeKind.Unspecified)
                let dayStart = TimeZoneInfo.ConvertTimeToUtc(easternDate, eastern)
                let dayEnd = TimeZoneInfo.ConvertTimeToUtc(easternDate.AddDays(1.0), eastern)
                let sportLower = sport.ToLower()

                let espnIdList = espnEventIds.ToList()

                let! staleGames =
                    db.Games
                        .Where(fun g ->
                            g.Sport = sportLower
                            && g.Status = "scheduled"
                            && g.ScheduledStart >= dayStart
                            && g.ScheduledStart < dayEnd
                            && not (espnIdList.Contains(g.Id)))
                        .ToListAsync(ct)

                for game in staleGames do
                    let prior = PlayoffSeriesObservation.Parse(game.SeriesObservationJson)
                    if isNull prior || prior.ObservedAt < seriesObservedAt then
                        if supportsSeries then
                            seriesObservations.Add(PlayoffSeriesCollector.Observe(game, scoreboard.RawJson, seriesObservedAt))
                        game.Status <- "canceled"
                        publishGameIds.Add(game.Id) |> ignore
                        logger.LogInformation(
                            "Canceled stale game {GameId} ({Away} @ {Home}) - no longer on ESPN scoreboard",
                            game.Id, game.AwayTeam, game.HomeTeam)
            | _ -> ()

            let! rejectedSeriesIds = PlayoffSeriesCollector.SaveAsync(db, seriesObservations, suppliedSeriesIds, ct)
            publishGameIds.ExceptWith(rejectedSeriesIds)
            for struct (evidence, allowOrdinaryDiscovery) in completionObservations do
                if not (rejectedSeriesIds.Contains(evidence.EventId)) then
                    let! _ = evidence.RecordAsync(db, allowOrdinaryDiscovery, ct)
                    ()
            logger.LogInformation("Stored {Count} odds snapshots for {Sport}", snapshots.Count, sport)

            let publishGameIdList = publishGameIds |> Seq.toList
            let! excitementScores =
                match sportLower with
                | "nfl" | "nba" | "nhl" | "mlb" when not publishGameIdList.IsEmpty ->
                    GameDataService(db, timeProvider).GetExcitementScoresForGamesAsync(publishGameIdList, ct)
                | _ -> Task.FromResult(Dictionary<string, Nullable<float>>() :> IDictionary<string, Nullable<float>>)
            let currentSnapshotIds = snapshots |> Seq.map (fun snapshot -> snapshot.GameId) |> Set.ofSeq
            let missingSnapshotIds = publishGameIdList |> List.filter (fun gameId -> not (currentSnapshotIds.Contains gameId))

            let! storedSnapshots =
                if missingSnapshotIds.IsEmpty then
                    Task.FromResult(Collections.Generic.List<OddsSnapshot>())
                else
                    db.OddsSnapshots
                        .Where(fun snapshot -> missingSnapshotIds.Contains(snapshot.GameId))
                        .ToListAsync(ct)

            let snapshotByGame =
                Seq.append snapshots storedSnapshots
                |> Seq.groupBy (fun snapshot -> snapshot.GameId)
                |> Seq.map (fun (gameId, gameSnapshots) -> gameId, gameSnapshots |> Seq.maxBy (fun snapshot -> struct (snapshot.Timestamp, snapshot.Id)))
                |> dict

            for gameId in publishGameIdList do
                let! game = db.Games.FindAsync([| gameId :> obj |], ct)

                if not (isNull game) then
                    let sportEnum = SportExtensions.FromDbValue(game.Sport)
                    let statusEnum = GameStatusExtensions.FromDbValue(game.Status)
                    let hasSnapshot, snapshot = snapshotByGame.TryGetValue(gameId)
                    let hasCurrentSnapshot = currentSnapshotIds.Contains(gameId)
                    let hasStoredAdvanceSnapshot =
                        hasSnapshot
                        && not snapshot.DrawWinProbability.HasValue
                        && snapshot.HomeMoneyline <> 0
                        && snapshot.AwayMoneyline <> 0
                    let useSnapshotForOdds =
                        hasSnapshot && backend.Services.GameWinnerProbabilityPolicy.FreshForDisplay(snapshot, now)
                        && (not game.IsOvertime || hasCurrentSnapshot || hasStoredAdvanceSnapshot)

                    if sportEnum.HasValue && statusEnum.HasValue then
                        let homeScore = if hasCurrentSnapshot then snapshot.HomeScore else game.HomeScore
                        let awayScore = if hasCurrentSnapshot then snapshot.AwayScore else game.AwayScore
                        let homeWinProbability = if useSnapshotForOdds then Nullable<float>(snapshot.HomeWinProbability) else Nullable<float>()
                        let awayWinProbability = if useSnapshotForOdds then Nullable<float>(snapshot.AwayWinProbability) else Nullable<float>()
                        let drawWinProbability = if useSnapshotForOdds then snapshot.DrawWinProbability else Nullable<float>()
                        let gameClock = if hasCurrentSnapshot then snapshot.GameClock else null
                        let period = if hasCurrentSnapshot then snapshot.Period else Nullable<int>()
                        let updatedAt = if useSnapshotForOdds then snapshot.Timestamp else now

                        let update =
                            GameUpdate(
                                GameId = game.Id,
                                Sport = sportEnum.Value,
                                HomeTeam = game.HomeTeam,
                                AwayTeam = game.AwayTeam,
                                HomeTeamAbbreviation = game.HomeTeamAbbreviation,
                                AwayTeamAbbreviation = game.AwayTeamAbbreviation,
                                HomeScore = homeScore,
                                AwayScore = awayScore,
                                Status = statusEnum.Value,
                                IsOvertime = game.IsOvertime,
                                HomeWinProbability = homeWinProbability,
                                AwayWinProbability = awayWinProbability,
                                DrawWinProbability = drawWinProbability,
                                OddsSource = (if useSnapshotForOdds then snapshot.Source else null),
                                ExcitementScore = (match excitementScores.TryGetValue(gameId) with true, score -> score | _ -> Nullable<float>()),
                                GameClock = gameClock,
                                Period = period,
                                UpdatedAt = updatedAt
                            )

                        do! gameUpdateService.PublishUpdateAsync(update, ct)

            return snapshots
        }
