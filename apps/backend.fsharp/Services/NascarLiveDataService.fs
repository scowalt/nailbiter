namespace backend.Services.Nascar

open System
open System.Collections.Generic
open System.Linq
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.EntityFrameworkCore
open backend.Data
open backend.Models
open backend.Services.EspnNascar
open backend.Services.Kalshi

type NascarLiveDataService
    (espnNascar: EspnNascarService,
     kalshiNascar: KalshiNascarOddsService,
     liveFeed: NascarLiveFeedService,
     serviceProvider: IServiceProvider,
     logger: ILogger<NascarLiveDataService>) =

    static member private CalculateExcitementScore
        (db: ExcitementDbContext, raceId: string, ct: CancellationToken)
        : Task<Nullable<float>> =
        task {
            let! snapshots =
                db.NascarRaceSnapshots
                    .FromSqlInterpolated(
                        $"SELECT * FROM nascar_race_snapshots WHERE race_id = {raceId} AND position = 1 AND win_probability IS NOT NULL ORDER BY \"timestamp\"")
                    .ToListAsync(ct)

            let probs =
                snapshots
                |> Seq.map (fun s -> s.WinProbability.Value)
                |> Seq.toArray

            if probs.Length < 2 then
                return Nullable<float>()
            else
                let mutable totalChange = 0.0

                for i in 1 .. probs.Length - 1 do
                    totalChange <- totalChange + Math.Abs(probs.[i] - probs.[i - 1])

                return Nullable<float>(Math.Min(100.0, totalChange * 20.0))
        }

    member private this.ProcessLiveRace
        (
            db: ExcitementDbContext,
            race: NascarRace,
            drivers: ResizeArray<NascarDriver>,
            kalshiProbs: Dictionary<string, double>,
            competitors: ResizeArray<EspnNascarCompetitor>,
            pitStatus: Dictionary<string, bool>,
            ct: CancellationToken
        ) : Task =
        task {
            let now = DateTime.UtcNow

            for comp in competitors do
                let athleteId = comp.Id

                let mutable winProb: Nullable<float> = Nullable<float>()
                let driverName = if isNull (box comp.Athlete) then null else comp.Athlete.DisplayName

                if not (isNull driverName) then
                    let mutable prob = 0.0

                    if kalshiProbs.TryGetValue(driverName, &prob) then
                        winProb <- Nullable<float>(prob)
                    else
                        let parts = driverName.Split(' ')
                        let lastName = if parts.Length > 0 then parts.[parts.Length - 1] else null

                        if not (isNull lastName) then
                            let matchKey =
                                kalshiProbs.Keys
                                |> Seq.tryFind (fun k ->
                                    k.EndsWith(lastName, StringComparison.OrdinalIgnoreCase))

                            match matchKey with
                            | Some key -> winProb <- Nullable<float>(kalshiProbs.[key])
                            | None -> ()

                let isOnTrack =
                    if
                        not (isNull (box comp.Vehicle))
                        && not (isNull comp.Vehicle.Number)
                    then
                        let mutable onTrack = false

                        if pitStatus.TryGetValue(comp.Vehicle.Number, &onTrack) then
                            Nullable<bool>(onTrack)
                        else
                            Nullable<bool>()
                    else
                        Nullable<bool>()

                db.NascarRaceSnapshots.Add(
                    NascarRaceSnapshot(
                        RaceId = race.Id,
                        EspnAthleteId = athleteId,
                        Timestamp = now,
                        Position = comp.Order,
                        WinProbability = winProb,
                        CompletedLaps = race.CompletedLaps,
                        Source = (if winProb.HasValue then "kalshi" else "espn"),
                        IsOnTrack = isOnTrack
                    )
                )
                |> ignore

                if not (isNull (box comp.Vehicle)) && not (isNull comp.Vehicle.Number) then
                    let driver =
                        drivers |> Seq.tryFind (fun d -> d.EspnAthleteId = athleteId)

                    match driver with
                    | Some driver when isNull driver.CarNumber ->
                        driver.CarNumber <- comp.Vehicle.Number
                        driver.Manufacturer <- comp.Vehicle.Manufacturer
                        driver.TeamName <- comp.Vehicle.Team
                    | _ -> ()

            let! excitementScore =
                NascarLiveDataService.CalculateExcitementScore(db, race.Id, ct)

            race.ExcitementScore <- excitementScore

            logger.LogDebug(
                "Stored {Count} snapshots for race {RaceId}, excitement={Score:F1}",
                competitors.Count,
                race.Id,
                race.ExcitementScore
            )
        }
        :> Task

    member this.RefreshLiveDataAsync(?ct: CancellationToken) : Task =
        let ct = defaultArg ct CancellationToken.None

        task {
            use scope = serviceProvider.CreateScope()
            let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()

            let! inProgressRaces =
                db.NascarRaces
                    .Where(fun r -> r.Status = "in_progress")
                    .ToListAsync(ct)

            if inProgressRaces.Count > 0 then
                let! scoreboard = espnNascar.FetchScoreboardByDateAsync(DateTime.UtcNow)

                let! liveFeedData = liveFeed.GetLiveFeedAsync(ct)

                let! kalshiProbs = kalshiNascar.GetWinProbabilitiesAsync(ct)

                let pitStatus =
                    if not (isNull (box liveFeedData)) && not (isNull liveFeedData.Vehicles) then
                        liveFeedData.Vehicles
                        |> Seq.map (fun v -> KeyValuePair(v.VehicleNumber, v.IsOnTrack))
                        |> fun pairs ->
                            Dictionary<string, bool>(pairs, StringComparer.OrdinalIgnoreCase)
                    else
                        Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)

                let season = DateTime.UtcNow.Year

                let! drivers =
                    db.NascarDrivers.Where(fun d -> d.Season = season).ToListAsync(ct)

                for race in inProgressRaces do
                    ct.ThrowIfCancellationRequested()

                    try
                        let eventData =
                            if isNull (box scoreboard) then
                                None
                            else
                                scoreboard.Events
                                |> Seq.tryFind (fun e -> e.Id = race.Id)

                        let competitors =
                            match eventData with
                            | Some ed when ed.Competitions.Count > 0 ->
                                ed.Competitions.[0].Competitors
                            | _ -> null

                        if isNull competitors || competitors.Count = 0 then
                            logger.LogDebug(
                                "No competitor data in scoreboard for race {RaceId}",
                                race.Id
                            )
                        else
                            if not (isNull (box liveFeedData)) then
                                race.FlagState <- Nullable<int>(liveFeedData.FlagState)

                                if liveFeedData.LapsInRace > 0 then
                                    race.TotalLaps <- liveFeedData.LapsInRace

                            do!
                                this.ProcessLiveRace(
                                    db,
                                    race,
                                    drivers,
                                    kalshiProbs,
                                    competitors,
                                    pitStatus,
                                    ct
                                )
                    with
                    | :? OperationCanceledException -> logger.LogDebug("Live data processing cancelled for race {RaceId}", race.Id)
                    | ex ->
                        logger.LogError(
                            ex,
                            "Error processing live data for race {RaceId}",
                            race.Id
                        )

                let! _ = db.SaveChangesAsync(ct)
                ()
        }
        :> Task
