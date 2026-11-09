namespace backend.Services

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open backend.Services.Espn
open backend.Services.PowerRankingsGuru

type RatingsRefreshBackgroundService(serviceProvider: IServiceProvider, logger: ILogger<RatingsRefreshBackgroundService>) =
    inherit BackgroundService()

    let refreshTime = TimeSpan.FromHours(6.0)

    member private _.RefreshAllStandingsAsync(scope: IServiceScope, ct: CancellationToken) =
        task {
            let standings = scope.ServiceProvider.GetRequiredService<EspnStandingsService>()
            logger.LogInformation("Starting standings refresh for all sports...")

            try let! _ = standings.RefreshNflStandingsAsync(ct) in ()
            with ex when not ct.IsCancellationRequested -> logger.LogError(ex, "Failed to refresh NFL standings")

            try let! _ = standings.RefreshNbaStandingsAsync(ct) in ()
            with ex when not ct.IsCancellationRequested -> logger.LogError(ex, "Failed to refresh NBA standings")

            try let! _ = standings.RefreshNhlStandingsAsync(ct) in ()
            with ex when not ct.IsCancellationRequested -> logger.LogError(ex, "Failed to refresh NHL standings")

            try let! _ = standings.RefreshMlbStandingsAsync(ct) in ()
            with ex when not ct.IsCancellationRequested -> logger.LogError(ex, "Failed to refresh MLB standings")

            logger.LogInformation("Standings refresh completed for all sports")
        }

    member this.RefreshAllRatingsAsync(?ct: CancellationToken) =
        let ct = defaultArg ct CancellationToken.None
        task {
            logger.LogInformation("Starting ratings refresh for all sports...")
            use scope = serviceProvider.CreateScope()
            let powerRankingsGuru = scope.ServiceProvider.GetRequiredService<PowerRankingsGuruService>()

            try let! _ = powerRankingsGuru.RefreshNflRatingsAsync(ct) in ()
            with ex when not ct.IsCancellationRequested -> logger.LogError(ex, "Failed to refresh PowerRankingsGuru NFL ratings")

            try let! _ = powerRankingsGuru.RefreshNbaRatingsAsync(ct) in ()
            with ex when not ct.IsCancellationRequested -> logger.LogError(ex, "Failed to refresh PowerRankingsGuru NBA ratings")

            try let! _ = powerRankingsGuru.RefreshNhlRatingsAsync(ct) in ()
            with ex when not ct.IsCancellationRequested -> logger.LogError(ex, "Failed to refresh PowerRankingsGuru NHL rankings")

            try let! _ = powerRankingsGuru.RefreshMlbRatingsAsync(ct) in ()
            with ex when not ct.IsCancellationRequested -> logger.LogError(ex, "Failed to refresh PowerRankingsGuru MLB ratings")

            let espnPowerIndex = scope.ServiceProvider.GetRequiredService<EspnPowerIndexService>()

            try let! _ = espnPowerIndex.RefreshNbaRatingsAsync(ct) in ()
            with ex when not ct.IsCancellationRequested -> logger.LogError(ex, "Failed to refresh ESPN BPI NBA ratings")

            try let! _ = espnPowerIndex.RefreshNflRatingsAsync(ct) in ()
            with ex when not ct.IsCancellationRequested -> logger.LogError(ex, "Failed to refresh ESPN FPI NFL ratings")

            do! this.RefreshAllStandingsAsync(scope, ct)
            logger.LogInformation("Ratings refresh completed for all sports")
        }

    override this.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            logger.LogInformation("Ratings refresh background service starting")
            do! Task.Delay(TimeSpan.FromSeconds(10.0), stoppingToken)
            do! this.RefreshAllRatingsAsync(stoppingToken)

            while not stoppingToken.IsCancellationRequested do
                try
                    let now = DateTime.UtcNow
                    let mutable nextRun = now.Date.Add(refreshTime)
                    if now >= nextRun then nextRun <- nextRun.AddDays(1.0)
                    let delay = nextRun - now
                    logger.LogInformation("Next ratings refresh scheduled for {NextRun} (in {Delay})", nextRun, delay)
                    do! Task.Delay(delay, stoppingToken)
                    do! this.RefreshAllRatingsAsync(stoppingToken)
                with
                | :? OperationCanceledException when stoppingToken.IsCancellationRequested -> logger.LogDebug("Ratings refresh cycle cancelled")
                | ex ->
                    logger.LogError(ex, "Error during ratings refresh cycle")
                    do! Task.Delay(TimeSpan.FromHours(1.0), stoppingToken)
        }
