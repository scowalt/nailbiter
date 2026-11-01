namespace backend.Services.F1

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

type F1BackgroundService(serviceProvider: IServiceProvider, logger: ILogger<F1BackgroundService>) =
    inherit BackgroundService()

    let pollInterval = TimeSpan.FromSeconds(30.0)

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            logger.LogInformation("F1 background service starting (interval: {Interval}s)", pollInterval.TotalSeconds)
            do! Task.Delay(TimeSpan.FromSeconds(10.0), stoppingToken)

            while not stoppingToken.IsCancellationRequested do
                try
                    use scope = serviceProvider.CreateScope()
                    let f1Service = scope.ServiceProvider.GetRequiredService<F1DataService>()
                    let liveDataService = scope.ServiceProvider.GetRequiredService<F1LiveDataService>()

                    let year = DateTime.UtcNow.Year
                    let! _ = f1Service.RefreshScheduleAsync(year, stoppingToken)
                    let! _ = f1Service.RefreshDriversAsync(year, stoppingToken)
                    let! _ = f1Service.RefreshChampionshipAsync(year, stoppingToken)
                    do! liveDataService.RefreshLiveDataAsync(stoppingToken)
                with
                | :? OperationCanceledException -> logger.LogDebug("F1 background refresh cycle cancelled")
                | ex ->
                    logger.LogError(ex, "Error during F1 background refresh cycle")

                do! Task.Delay(pollInterval, stoppingToken)
        }
