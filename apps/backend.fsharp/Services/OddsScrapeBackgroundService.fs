namespace backend.Services

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open backend.Services.Espn

type OddsScrapeBackgroundService
    (serviceProvider: IServiceProvider, timeProvider: TimeProvider, logger: ILogger<OddsScrapeBackgroundService>) =
    inherit BackgroundService()

    let liveOddsInterval = TimeSpan.FromSeconds(30.0)
    let pregameOddsInterval = TimeSpan.FromMinutes(30.0)
    let sports = [| "world_cup"; "nba"; "nfl"; "nhl"; "mlb" |]

    member private this.ScrapeLiveOddsAsync(ct: CancellationToken) =
        task {
            use scope = serviceProvider.CreateScope()
            let espnService = scope.ServiceProvider.GetRequiredService<EspnOddsService>()

            for sport in sports do
                try
                    let! snapshots = espnService.ScrapeAndStoreOddsAsync(sport, ct)
                    if snapshots.Count > 0 then
                        logger.LogInformation("Scraped {Count} live odds for {Sport}", snapshots.Count, sport)
                with
                | :? OperationCanceledException -> logger.LogDebug("Live odds scraping cancelled for {Sport}", sport)
                | ex ->
                    logger.LogError(ex, "Failed to scrape live odds for {Sport}", sport)
        }

    member private this.RefreshPregameOddsAsync(ct: CancellationToken) =
        task {
            use scope = serviceProvider.CreateScope()
            let pregameService = scope.ServiceProvider.GetRequiredService<PregameOddsService>()

            try
                let! updated = pregameService.RefreshPregameOddsAsync(ct)
                if updated > 0 then
                    logger.LogInformation("Refreshed pregame odds for {Count} scheduled games", updated)
            with
            | :? OperationCanceledException -> logger.LogDebug("Pregame odds refresh cancelled")
            | ex ->
                logger.LogError(ex, "Failed to refresh pregame odds")
        }

    member private this.LiveOddsLoopAsync(ct: CancellationToken) =
        task {
            logger.LogInformation("Live odds polling loop starting (interval: {Interval}s)", liveOddsInterval.TotalSeconds)
            while not ct.IsCancellationRequested do
                try
                    do! this.ScrapeLiveOddsAsync(ct)
                with
                | :? OperationCanceledException -> logger.LogDebug("Live odds scraping cycle cancelled")
                | ex ->
                    logger.LogError(ex, "Error during live odds scraping cycle")
                do! Task.Delay(liveOddsInterval, timeProvider, ct)
        }

    member private this.PregameOddsLoopAsync(ct: CancellationToken) =
        task {
            logger.LogInformation("Pregame odds polling loop starting (interval: {Interval}m)", pregameOddsInterval.TotalMinutes)
            while not ct.IsCancellationRequested do
                try
                    do! this.RefreshPregameOddsAsync(ct)
                with
                | :? OperationCanceledException -> logger.LogDebug("Pregame odds refresh cycle cancelled")
                | ex ->
                    logger.LogError(ex, "Error during pregame odds refresh cycle")
                do! Task.Delay(pregameOddsInterval, timeProvider, ct)
        }

    override this.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            logger.LogInformation("Odds scraper background service starting")
            do! Task.Delay(TimeSpan.FromSeconds(5.0), timeProvider, stoppingToken)
            let! _ = Task.WhenAll(this.LiveOddsLoopAsync(stoppingToken) :> Task, this.PregameOddsLoopAsync(stoppingToken) :> Task)
            ()
        }
