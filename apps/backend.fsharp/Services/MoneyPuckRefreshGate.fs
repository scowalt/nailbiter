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
    static member internal Interval = TimeSpan.FromHours(24.0)
    member internal _.NextAttempt = nextAttempt
    member internal _.TryEnterAsync(ct: CancellationToken) = task {
        let! entered = gate.WaitAsync(0, ct)
        if not entered then return false
        else
            try
                let now = timeProvider.GetUtcNow().UtcDateTime
                if denied || (nextAttempt.HasValue && now < nextAttempt.Value) then
                    gate.Release() |> ignore
                    return false
                else
                    let due = now.Add(MoneyPuckRefreshGate.Interval)
                    use scope = serviceProvider.CreateScope()
                    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
                    let sql: FormattableString = $"INSERT INTO moneypuck_refresh_state (id, next_attempt_at_ticks, active, denied) VALUES (1, {due.Ticks}, TRUE, FALSE) ON CONFLICT (id) DO UPDATE SET next_attempt_at_ticks = EXCLUDED.next_attempt_at_ticks, active = TRUE WHERE NOT moneypuck_refresh_state.active AND NOT moneypuck_refresh_state.denied AND moneypuck_refresh_state.next_attempt_at_ticks <= {now.Ticks}"
                    let! claimed = db.Database.ExecuteSqlInterpolatedAsync(sql, ct)
                    if claimed = 1 then
                        nextAttempt <- Nullable due
                        return true
                    else
                        let! state = db.MoneyPuckRefreshOperations.AsNoTracking().SingleAsync(ct)
                        denied <- state.Denied
                        let due = DateTime(state.NextAttemptAtTicks, DateTimeKind.Utc)
                        nextAttempt <- if denied then Nullable() elif due > now then Nullable due else Nullable(now.Add(MoneyPuckRefreshGate.Interval))
                        gate.Release() |> ignore
                        return false
            with
            | :? OperationCanceledException when ct.IsCancellationRequested ->
                gate.Release() |> ignore
                return raise (OperationCanceledException(ct))
            | ex ->
                gate.Release() |> ignore
                logger.LogWarning(ex, "MoneyPuck daily admission unavailable; no provider requests permitted")
                return false
    }
    member internal _.LatchDenial() = denied <- true
    member internal _.CompleteAsync() = task {
        try
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10.0))
            use scope = serviceProvider.CreateScope()
            let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
            let sql: FormattableString = $"UPDATE moneypuck_refresh_state SET active = FALSE, denied = {denied} WHERE id = 1"
            let! _ = db.Database.ExecuteSqlInterpolatedAsync(sql, timeout.Token)
            if denied then nextAttempt <- Nullable()
        with ex ->
            logger.LogWarning(ex, "MoneyPuck cycle finalization unavailable; persisted admission remains blocked pending review")
    }
    member internal _.Exit() = gate.Release() |> ignore
