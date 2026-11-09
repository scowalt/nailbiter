namespace backend.Services

open System
open System.Threading
open System.Threading.Tasks
open backend.Data
open Microsoft.EntityFrameworkCore
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging

[<AllowNullLiteral>]
type MoneyPuckRefreshGate(serviceProvider: IServiceProvider, timeProvider: TimeProvider, logger: ILogger<MoneyPuckRefreshGate>) =
    let gate = new SemaphoreSlim(1, 1)
    let mutable nextAttempt = Nullable<DateTime>()
    let mutable denied = false
    let mutable deniedUntil = Nullable<DateTime>()
    static member internal Interval = TimeSpan.FromHours(24.0)
    member internal _.NextAttempt = nextAttempt
    member internal _.TryEnterAsync(ct: CancellationToken) = task {
        let! entered = gate.WaitAsync(0, ct)
        if not entered then return false
        else
            try
                let now = timeProvider.GetUtcNow().UtcDateTime
                if nextAttempt.HasValue && now < nextAttempt.Value then
                    gate.Release() |> ignore
                    return false
                else
                    let due = now.Add(MoneyPuckRefreshGate.Interval)
                    use scope = serviceProvider.CreateScope()
                    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
                    let sql: FormattableString = $"INSERT INTO moneypuck_refresh_state (id, next_attempt_at_ticks, active, denied, denied_until_ticks) VALUES (1, {due.Ticks}, TRUE, FALSE, NULL) ON CONFLICT (id) DO UPDATE SET next_attempt_at_ticks = EXCLUDED.next_attempt_at_ticks, active = TRUE, denied = FALSE, denied_until_ticks = NULL WHERE NOT moneypuck_refresh_state.active AND moneypuck_refresh_state.next_attempt_at_ticks <= {now.Ticks} AND (NOT moneypuck_refresh_state.denied OR moneypuck_refresh_state.denied_until_ticks <= {now.Ticks})"
                    let! claimed = db.Database.ExecuteSqlInterpolatedAsync(sql, ct)
                    if claimed = 1 then
                        nextAttempt <- Nullable due
                        denied <- false
                        deniedUntil <- Nullable()
                        return true
                    else
                        let! state = db.MoneyPuckRefreshOperations.AsNoTracking().SingleAsync(ct)
                        denied <- state.Denied
                        deniedUntil <-
                            if denied && state.DeniedUntilTicks.HasValue then Nullable(DateTime(state.DeniedUntilTicks.Value, DateTimeKind.Utc))
                            else Nullable()
                        let dailyDue = DateTime(state.NextAttemptAtTicks, DateTimeKind.Utc)
                        let due = if deniedUntil.HasValue then max dailyDue deniedUntil.Value else dailyDue
                        nextAttempt <- if due > now then Nullable due else Nullable(now.Add(MoneyPuckRefreshGate.Interval))
                        gate.Release() |> ignore
                        return false
            with
            | :? OperationCanceledException when ct.IsCancellationRequested ->
                gate.Release() |> ignore
                return raise (OperationCanceledException(ct))
            | ex ->
                gate.Release() |> ignore
                logger.LogError(ex, "MoneyPuck daily admission unavailable; no provider requests permitted")
                return false
    }
    member internal _.LatchDenial(?retryAfter: DateTime) =
        denied <- true
        let minimum = timeProvider.GetUtcNow().UtcDateTime.Add(MoneyPuckRefreshGate.Interval)
        let due = retryAfter |> Option.map (max minimum) |> Option.defaultValue minimum
        deniedUntil <- Nullable due
        nextAttempt <- Nullable(if nextAttempt.HasValue then max nextAttempt.Value due else due)
    member internal _.CompleteAsync() = task {
        try
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10.0))
            use scope = serviceProvider.CreateScope()
            let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
            let untilTicks = if deniedUntil.HasValue then Nullable deniedUntil.Value.Ticks else Nullable<int64>()
            let sql: FormattableString = $"UPDATE moneypuck_refresh_state SET active = FALSE, denied = {denied}, denied_until_ticks = {untilTicks}, next_attempt_at_ticks = GREATEST(next_attempt_at_ticks, {nextAttempt.Value.Ticks}) WHERE id = 1"
            let! _ = db.Database.ExecuteSqlInterpolatedAsync(sql, timeout.Token)
            ()
        with ex ->
            logger.LogError(ex, "MoneyPuck cycle finalization unavailable; persisted admission remains blocked pending review")
    }
    member internal _.Exit() = gate.Release() |> ignore
