namespace backend.Services.Nascar

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open backend.Models

type internal RosterAttempt =
    { Race: string
      Status: string
      RetryAt: DateTimeOffset option }

type NascarBackgroundService
    (serviceProvider: IServiceProvider, logger: ILogger<NascarBackgroundService>, timeProvider: TimeProvider) =
    inherit BackgroundService()

    let pollInterval = TimeSpan.FromSeconds(30.0)
    let rosterRetry = TimeSpan.FromHours(1.0)
    let mutable lastRosterAttempt: RosterAttempt option = None

    let mayFetchRoster (race: NascarRace) now =
        match lastRosterAttempt with
        | Some attempt when attempt.Race = race.Id && attempt.Status = race.Status ->
            attempt.RetryAt |> Option.exists (fun retryAt -> now >= retryAt)
        | _ -> true

    let refreshRosterAsync (nascarService: NascarDataService) year (ct: CancellationToken) =
        task {
            let now = timeProvider.GetUtcNow()
            let! stale = nascarService.FindStaleRosterRaceAsync(year, now.UtcDateTime, ct)

            match stale with
            | Some race when mayFetchRoster race now ->
                let attempt = { Race = race.Id; Status = race.Status; RetryAt = Some(now + rosterRetry) }
                lastRosterAttempt <- Some attempt
                let! stored = nascarService.RefreshDriversAsync(race, ct)

                if stored > 0 then
                    lastRosterAttempt <- Some { attempt with RetryAt = None }
            | _ -> ()
        }

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            logger.LogInformation("NASCAR background service starting (interval: {Interval}s)", pollInterval.TotalSeconds)
            do! Task.Delay(TimeSpan.FromSeconds(10.0), timeProvider, stoppingToken)

            while not stoppingToken.IsCancellationRequested do
                try
                    use scope = serviceProvider.CreateScope()
                    let nascarService = scope.ServiceProvider.GetRequiredService<NascarDataService>()
                    let liveDataService = scope.ServiceProvider.GetRequiredService<NascarLiveDataService>()

                    let year = timeProvider.GetUtcNow().Year
                    let! _ = nascarService.RefreshScheduleAsync(year, stoppingToken)
                    let! _ = nascarService.RefreshRaceResultsAsync(year, stoppingToken)
                    let! _ = nascarService.RefreshStartingGridAsync(year, stoppingToken)
                    do! liveDataService.RefreshLiveDataAsync(stoppingToken)
                    do! refreshRosterAsync nascarService year stoppingToken
                with
                | :? OperationCanceledException -> logger.LogDebug("NASCAR schedule refresh cycle cancelled")
                | ex ->
                    logger.LogError(ex, "Error during NASCAR schedule refresh cycle")

                do! Task.Delay(pollInterval, timeProvider, stoppingToken)
        }
