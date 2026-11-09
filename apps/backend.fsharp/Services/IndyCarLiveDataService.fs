namespace backend.Services.IndyCar

open System
open System.Linq
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.EntityFrameworkCore
open backend.Data
open backend.Models
open backend.Services.EspnIndyCar
open backend.Services.EspnNascar

type IndyCarLiveDataService
    (espnIndyCar: EspnIndyCarService,
     serviceProvider: IServiceProvider,
     logger: ILogger<IndyCarLiveDataService>) =

    static member private CalculateExcitementScore
        (db: ExcitementDbContext, raceId: string, ct: CancellationToken)
        : Task<Nullable<float>> =
        task {
            let! snapshots =
                db.IndyCarRaceSnapshots
                    .FromSqlInterpolated(
                        $"SELECT * FROM indycar_race_snapshots WHERE race_id = {raceId} AND position = 1 ORDER BY \"timestamp\"")
                    .ToListAsync(ct)

            let leaderHistory = snapshots |> Seq.map (fun s -> s.EspnAthleteId) |> Seq.toArray

            if leaderHistory.Length < 2 then
                return Nullable<float>()
            else
                let mutable leadChanges = 0

                for i in 1 .. leaderHistory.Length - 1 do
                    if leaderHistory.[i] <> leaderHistory.[i - 1] then
                        leadChanges <- leadChanges + 1

                return Nullable<float>(Math.Min(100.0, float leadChanges * 5.0))
        }

    member private this.ProcessLiveRace
        (
            db: ExcitementDbContext,
            race: IndyCarRace,
            competitors: ResizeArray<EspnNascarCompetitor>,
            ct: CancellationToken
        ) : Task =
        task {
            let now = DateTime.UtcNow

            for comp in competitors do
                let athleteId = comp.Id

                db.IndyCarRaceSnapshots.Add(
                    IndyCarRaceSnapshot(
                        RaceId = race.Id,
                        EspnAthleteId = athleteId,
                        Timestamp = now,
                        Position = comp.Order,
                        WinProbability = Nullable<float>(),
                        CompletedLaps = race.CompletedLaps,
                        Source = "espn"
                    )
                )
                |> ignore

            let! excitementScore =
                IndyCarLiveDataService.CalculateExcitementScore(db, race.Id, ct)

            race.ExcitementScore <- excitementScore

            logger.LogDebug(
                "Stored {Count} snapshots for IndyCar race {RaceId}, excitement={Score}",
                competitors.Count,
                race.Id,
                (if race.ExcitementScore.HasValue then
                     race.ExcitementScore.Value.ToString("F1")
                 else
                     "N/A")
            )
        }
        :> Task

    member this.RefreshLiveDataAsync(?ct: CancellationToken) : Task =
        let ct = defaultArg ct CancellationToken.None

        task {
            use scope = serviceProvider.CreateScope()
            let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()

            let! inProgressRaces =
                db.IndyCarRaces
                    .Where(fun r -> r.Status = "in_progress")
                    .ToListAsync(ct)

            if inProgressRaces.Count > 0 then
                let! scoreboard = espnIndyCar.FetchScoreboardByDateAsync(DateTime.UtcNow, ct = ct)

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
                            | Some ed when ed.Competitions.Count > 0 ->
                                ed.Competitions.[0].Competitors
                            | _ -> null

                        if isNull competitors || competitors.Count = 0 then
                            logger.LogDebug(
                                "No competitor data in scoreboard for IndyCar race {RaceId}",
                                race.Id
                            )
                        else
                            match eventData with
                            | Some ed when
                                not (isNull (box ed.Status))
                                && ed.Status.Period.HasValue
                                && ed.Status.Period.Value > 0
                                ->
                                race.CompletedLaps <- ed.Status.Period

                                if race.TotalLaps = 0 then
                                    race.TotalLaps <- ed.Status.Period.Value
                            | _ -> ()

                            do! this.ProcessLiveRace(db, race, competitors, ct)
                    with
                    | :? OperationCanceledException -> logger.LogDebug("Live data processing cancelled for IndyCar race {RaceId}", race.Id)
                    | ex ->
                        logger.LogError(
                            ex,
                            "Error processing live data for IndyCar race {RaceId}",
                            race.Id
                        )

                let! _ = db.SaveChangesAsync(ct)
                ()
        }
        :> Task
