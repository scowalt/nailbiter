using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace backend.tests;

internal sealed class GameRowLockGate : DbCommandInterceptor
{
    private readonly AsyncTestGate gate = new();
    public TaskCompletionSource Entered => gate.Entered;
    public void Arm() => gate.Arm();
    public void Release() => gate.Release();

    public override async ValueTask<InterceptionResult> DataReaderClosingAsync(DbCommand command,
        DataReaderClosingEventData eventData, InterceptionResult result)
    {
        if (command.CommandText.Contains("FROM games WHERE id", StringComparison.Ordinal)
            && command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal)) await gate.EnterAsync(default);
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
