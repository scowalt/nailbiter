namespace backend.Services

open System
open System.Linq
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open backend.Data
open backend.Models
open Microsoft.EntityFrameworkCore
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

[<CLIMutable>]
type MoneyPuckRefreshResult =
    { Rankings: int
      Qualification: int
      NextAttemptAt: Nullable<DateTime> }

type MoneyPuckService(httpClient: HttpClient, logger: ILogger<MoneyPuckService>, serviceProvider: IServiceProvider, timeProvider: TimeProvider) =
    let gate = serviceProvider.GetRequiredService<MoneyPuckRefreshGate>()
    let options = serviceProvider.GetRequiredService<IOptions<PlayoffRatingsOptions>>()
    let source = MoneyPuckHttpClient(httpClient, timeProvider, gate)
    let qualification = MoneyPuckQualificationCollector(source, logger, serviceProvider, timeProvider, options)
    let now () = timeProvider.GetUtcNow().UtcDateTime

    member private _.RefreshRankingsAsync(ct: CancellationToken) = task {
        let! observation = task {
            try
                let! before = source.ReadAsync(MoneyPuckRankings.UpdatedUrl, 4096, ct)
                let! csv = task {
                    try return! source.ReadAsync(MoneyPuckRankings.RankingsUrl, 2 * 1024 * 1024, ct)
                    with
                    | MoneyPuckInvalidSource _ -> return ""
                    | :? DecoderFallbackException -> return ""
                }
                let! after = source.ReadAsync(MoneyPuckRankings.UpdatedUrl, 4096, ct)
                if before.Trim() <> after.Trim() then return None
                else return Some (MoneyPuckRankings.Observe(csv, after, now ()))
            with
            | MoneyPuckInvalidSource _ -> return Some (MoneyPuckRankingObservation(MoneyPuckRankingState.Rejected, null))
            | :? DecoderFallbackException -> return Some (MoneyPuckRankingObservation(MoneyPuckRankingState.Rejected, null))
        }
        match observation with
        | None -> return 0
        | Some observation ->
            use scope = serviceProvider.CreateScope()
            let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
            let! previous = db.TeamRatings.Where(fun r -> r.Sport = "nhl" && r.Source = MoneyPuckRankings.Source).ToListAsync(ct)
            let fetched = now ()
            let snapshot = observation.Snapshot
            if observation.State = MoneyPuckRankingState.Rejected then
                for row in previous do
                    row.PowerRank <- Nullable()
                    row.SourceSeason <- Nullable()
                    row.RankPublishedAt <- Nullable()
                    row.FetchedAt <- fetched
                let! _ = db.SaveChangesAsync(ct)
                logger.LogWarning("Rejected MoneyPuck ranking: incomplete/invalid CSV or missing, future or unsupported publication date")
                return 0
            else
                for team in snapshot.Teams do
                    let row =
                        match previous |> Seq.tryFind (fun r -> r.Season = snapshot.Season && r.TeamAbbreviation = team.Team) with
                        | Some row -> row
                        | None ->
                            let row = TeamRating(Sport = "nhl", TeamAbbreviation = team.Team, Source = MoneyPuckRankings.Source, Season = snapshot.Season)
                            db.TeamRatings.Add(row) |> ignore
                            row
                    row.PowerRank <- Nullable team.Rank
                    row.SourceSeason <- Nullable snapshot.Season
                    row.RankPublishedAt <- Nullable snapshot.PublishedAt
                    row.FetchedAt <- fetched
                let! _ = db.SaveChangesAsync(ct)
                logger.LogInformation("Observed {Count} MoneyPuck ranks, published {PublishedAt}, applicable season {Season}, state {State}", snapshot.Teams.Count, snapshot.PublishedAt, snapshot.Season, observation.State)
                return if observation.State = MoneyPuckRankingState.Accepted then snapshot.Teams.Count else 0
    }

    member internal this.RefreshForSeasonAsync(season: int, ct: CancellationToken) = task {
        let! entered = gate.TryEnterAsync(ct)
        if not entered then return { Rankings = 0; Qualification = 0; NextAttemptAt = gate.NextAttempt }
        else
            try
                let mutable rankings = 0
                let mutable forecasts = 0
                let mutable cancelled = false
                try
                    use deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90.0), timeProvider)
                    use attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token)
                    let! collected = qualification.RefreshAsync(season, attempt.Token)
                    forecasts <- collected
                    let! collected = this.RefreshRankingsAsync(attempt.Token)
                    rankings <- collected
                with
                | :? OperationCanceledException when ct.IsCancellationRequested -> cancelled <- true
                | ex -> logger.Log((if DatabaseFailure.Is ex then LogLevel.Error else LogLevel.Warning), ex, "MoneyPuck daily collection stopped; uncommitted input observations retained")
                do! gate.CompleteAsync()
                if cancelled then return raise (OperationCanceledException(ct))
                else return { Rankings = rankings; Qualification = forecasts; NextAttemptAt = gate.NextAttempt }
            finally gate.Exit()
    }

    member this.RefreshNhlAsync(ct: CancellationToken) =
        this.RefreshForSeasonAsync(PlayoffRatingPolicy.seasonAt "nhl" (now ()), ct)

    member this.RefreshNhlRatingsAsync(ct: CancellationToken) : Task<int> = task {
        let! result = this.RefreshNhlAsync(ct)
        return result.Rankings
    }

type MoneyPuckQualificationService(collection: MoneyPuckService, timeProvider: TimeProvider, optionsAccessor: IOptions<PlayoffRatingsOptions>) =
    member _.RefreshNhlRatingsForSeasonAsync(season: int, ct: CancellationToken) : Task<int> = task {
        if not (optionsAccessor.Value.MoneyPuckRefreshEnabledFor("nhl", season)) then return 0
        else
            let! result = collection.RefreshForSeasonAsync(season, ct)
            return result.Qualification
    }
    member this.RefreshNhlRatingsAsync(ct: CancellationToken) =
        this.RefreshNhlRatingsForSeasonAsync(PlayoffRatingPolicy.seasonAt "nhl" (timeProvider.GetUtcNow().UtcDateTime), ct)

type MoneyPuckRefreshBackgroundService(serviceProvider: IServiceProvider, logger: ILogger<MoneyPuckRefreshBackgroundService>, timeProvider: TimeProvider) =
    inherit Microsoft.Extensions.Hosting.BackgroundService()
    override _.ExecuteAsync(stoppingToken: CancellationToken) = task {
        while not stoppingToken.IsCancellationRequested do
            try
                use scope = serviceProvider.CreateScope()
                let service = scope.ServiceProvider.GetRequiredService<MoneyPuckService>()
                let! result = service.RefreshNhlAsync(stoppingToken)
                let delay = if result.NextAttemptAt.HasValue then result.NextAttemptAt.Value - timeProvider.GetUtcNow().UtcDateTime else MoneyPuckRefreshGate.Interval
                do! Task.Delay(max (TimeSpan.FromSeconds(1.0)) delay, timeProvider, stoppingToken)
            with
            | :? OperationCanceledException when stoppingToken.IsCancellationRequested -> ()
            | ex ->
                logger.LogWarning(ex, "MoneyPuck daily background cycle failed")
                do! Task.Delay(MoneyPuckRefreshGate.Interval, timeProvider, stoppingToken)
    }
