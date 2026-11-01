namespace backend.Services.IndyCar

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

type IndyCarBackgroundService(serviceProvider: IServiceProvider, logger: ILogger<IndyCarBackgroundService>) =
    inherit BackgroundService()

    let pollInterval = TimeSpan.FromSeconds(30.0)

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            logger.LogInformation("IndyCar background service starting (interval: {Interval}s)", pollInterval.TotalSeconds)
            do! Task.Delay(TimeSpan.FromSeconds(10.0), stoppingToken)

            while not stoppingToken.IsCancellationRequested do
                try
                    use scope = serviceProvider.CreateScope()
                    let indyCarService = scope.ServiceProvider.GetRequiredService<IndyCarDataService>()
                    let liveDataService = scope.ServiceProvider.GetRequiredService<IndyCarLiveDataService>()

                    let year = DateTime.UtcNow.Year
                    let! _ = indyCarService.RefreshScheduleAsync(year, stoppingToken)
                    let! _ = indyCarService.RefreshDriversAsync(year, stoppingToken)
                    let! _ = indyCarService.RefreshRaceResultsAsync(year, stoppingToken)
                    do! indyCarService.RefreshStartingGridAsync(year, stoppingToken)
                    do! liveDataService.RefreshLiveDataAsync(stoppingToken)
                with
                | :? OperationCanceledException -> logger.LogDebug("IndyCar background refresh cycle cancelled")
                | ex ->
                    logger.LogError(ex, "Error during IndyCar background refresh cycle")

                do! Task.Delay(pollInterval, stoppingToken)
        }
