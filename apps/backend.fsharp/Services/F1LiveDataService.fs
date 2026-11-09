namespace backend.Services.F1

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
open backend.Services.EspnF1
open backend.Services.EspnNascar
open backend.Services.Kalshi

type F1LiveDataService
    (espnF1: EspnF1Service,
     kalshiF1: KalshiF1OddsService,
     serviceProvider: IServiceProvider,
     logger: ILogger<F1LiveDataService>) =

    static member private CalculateExcitementScore
        (db: ExcitementDbContext, raceId: string, ct: CancellationToken)
        : Task<Nullable<float>> =
        task {
            let! snapshots =
                db.F1RaceSnapshots
                    .FromSqlInterpolated(
                        $"SELECT * FROM f1_race_snapshots WHERE race_id = {raceId} AND position = 1 AND win_probability IS NOT NULL ORDER BY \"timestamp\"")
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
            race: F1Race,
            drivers: ResizeArray<F1Driver>,
            kalshiProbs: Dictionary<string, double>,
            competitors: ResizeArray<EspnNascarCompetitor>,
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

                db.F1RaceSnapshots.Add(
                    F1RaceSnapshot(
                        RaceId = race.Id,
                        EspnAthleteId = athleteId,
                        Timestamp = now,
                        Position = comp.Order,
                        WinProbability = winProb,
                        CompletedLaps = race.CompletedLaps,
                        Source = (if winProb.HasValue then "kalshi" else "espn")
                    )
                )
                |> ignore

            let! excitementScore =
                F1LiveDataService.CalculateExcitementScore(db, race.Id, ct)

            race.ExcitementScore <- excitementScore

            logger.LogDebug(
                "Stored {Count} snapshots for F1 race {RaceId}, excitement={Score:F1}",
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
                db.F1Races
                    .Where(fun r -> r.Status = "in_progress")
                    .ToListAsync(ct)

            if inProgressRaces.Count > 0 then
                let! scoreboard = espnF1.FetchScoreboardAsync(ct = ct)

                let! kalshiProbs = kalshiF1.GetWinProbabilitiesAsync(ct)

                let season = DateTime.UtcNow.Year

                let! drivers =
                    db.F1Drivers.Where(fun d -> d.Season = season).ToListAsync(ct)

                for race in inProgressRaces do
                    ct.ThrowIfCancellationRequested()

                    try
                        let eventData =
                            if isNull (box scoreboard) then
                                None
                            else
                                scoreboard.Events
                                |> Seq.tryFind (fun e -> e.Id = race.EspnEventId)

                        let competitors =
                            match eventData with
                            | Some ed when not (isNull ed.Competitions) ->
                                let raceComp =
                                    ed.Competitions
                                    |> Seq.tryFind (fun c ->
                                        not (isNull (box c.Type))
                                        && not (isNull c.Type.Abbreviation)
                                        && c.Type.Abbreviation = "Race")

                                match raceComp with
                                | Some c -> c.Competitors
                                | None ->
                                    if ed.Competitions.Count > 0 then
                                        ed.Competitions.[ed.Competitions.Count - 1].Competitors
                                    else
                                        null
                            | _ -> null

                        if isNull competitors || competitors.Count = 0 then
                            logger.LogDebug(
                                "No competitor data in scoreboard for F1 race {RaceId}",
                                race.Id
                            )
                        else
                            do!
                                this.ProcessLiveRace(
                                    db,
                                    race,
                                    drivers,
                                    kalshiProbs,
                                    competitors,
                                    ct
                                )
                    with
                    | :? OperationCanceledException -> logger.LogDebug("Live data processing cancelled for F1 race {RaceId}", race.Id)
                    | ex ->
                        logger.LogError(
                            ex,
                            "Error processing live data for F1 race {RaceId}",
                            race.Id
                        )

                let! _ = db.SaveChangesAsync(ct)
                ()
        }
        :> Task
