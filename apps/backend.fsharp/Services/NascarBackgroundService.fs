namespace backend.Services.Nascar

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

type NascarBackgroundService
    (serviceProvider: IServiceProvider, logger: ILogger<NascarBackgroundService>, timeProvider: TimeProvider) =
    inherit BackgroundService()

    let pollInterval = TimeSpan.FromSeconds(30.0)

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
                with
                | :? OperationCanceledException -> logger.LogDebug("NASCAR schedule refresh cycle cancelled")
                | ex ->
                    logger.LogError(ex, "Error during NASCAR schedule refresh cycle")

                do! Task.Delay(pollInterval, timeProvider, stoppingToken)
        }
