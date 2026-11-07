using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace backend.tests;

internal sealed class GameUpdateGate : DbCommandInterceptor
{
    private readonly AsyncTestGate gate = new();
    public TaskCompletionSource Entered => gate.Entered;
    public void Arm() => gate.Arm();
    public void Release() => gate.Release();

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
    {
        if (command.CommandText.Contains("UPDATE games")) await gate.EnterAsync(ct);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (command.CommandText.Contains("UPDATE games")) await gate.EnterAsync(ct);
        return result;
    }

    public static async Task WaitForLockWaitAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var waiting = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", connection);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while ((long)(await waiting.ExecuteScalarAsync())! == 0)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("No session waited on a lock");
            await Task.Delay(20);
        }
    }
}
