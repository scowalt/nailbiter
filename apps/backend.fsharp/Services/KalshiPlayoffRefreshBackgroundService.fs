namespace backend.Services

open System
open System.Threading
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open backend.Data
open backend.Services.Kalshi

type KalshiPlayoffRefreshBackgroundService
    (serviceProvider: IServiceProvider, timeProvider: TimeProvider, logger: ILogger<KalshiPlayoffRefreshBackgroundService>) =
    inherit BackgroundService()

    override _.ExecuteAsync(ct: CancellationToken) = task {
        use timer = new PeriodicTimer(PlayoffRatingPolicy.refreshInterval, timeProvider)
        try
            let mutable running = true
            while running && not ct.IsCancellationRequested do
                try
                    use scope = serviceProvider.CreateScope()
                    let service = scope.ServiceProvider.GetRequiredService<KalshiPlayoffRatingsService>()
                    let! _ = service.RefreshCurrentAsync(ct)
                    ()
                with
                | :? OperationCanceledException when ct.IsCancellationRequested -> ()
                | ex -> logger.Log((if DatabaseFailure.Is ex then LogLevel.Error else LogLevel.Warning), ex, "Kalshi playoff refresh failed")
                let! tick = timer.WaitForNextTickAsync(ct)
                running <- tick
        with
        | :? OperationCanceledException when ct.IsCancellationRequested -> ()
    }
