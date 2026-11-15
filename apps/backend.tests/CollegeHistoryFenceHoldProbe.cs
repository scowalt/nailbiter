using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace backend.tests;

public sealed class CollegeHistoryFenceHoldProbe(ITestOutputHelper output)
{
    internal const string GamesVariable = "NAILBITER_COLLEGE_IMPORT_GAMES";

    [CollegeHistoryHoldFact]
    public async Task Measure_fence_hold_and_pro_writer_wait_for_each_import_size()
    {
        foreach (var games in Environment.GetEnvironmentVariable(GamesVariable)!.Split(',').Select(n => int.Parse(n, CultureInfo.InvariantCulture)))
        {
            await using var database = await CollegeHistoryDatabase.StartAsync(games);
            var holds = new FenceHoldRecorder();
            await using var probe = ProWriterProbe.Start(database.ConnectionString);
            var timer = Stopwatch.StartNew();
            await database.ImportAsync(holds);
            var elapsed = timer.Elapsed;
            await probe.StopAsync();
            var snapshots = await database.CountAsync("odds_snapshots");
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"games={games} snapshots={snapshots} units={holds.Holds.Count} maxHoldMs={holds.Holds.Max().TotalMilliseconds:F0} " +
                $"totalHoldMs={holds.Holds.Sum(h => h.TotalMilliseconds):F0} maxProWaitMs={probe.MaxWait.TotalMilliseconds:F0} probes={probe.Samples.Count} importMs={elapsed.TotalMilliseconds:F0}"));
        }
    }
}

public sealed class CollegeHistoryHoldFactAttribute : FactAttribute
{
    public CollegeHistoryHoldFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(CollegeHistoryFenceHoldProbe.GamesVariable) is null)
            Skip = "Measurement for issue #190; set NAILBITER_COLLEGE_IMPORT_GAMES=150,300 to run it";
    }
}

internal sealed class FenceHoldRecorder : DbCommandInterceptor, IDbTransactionInterceptor
{
    private long fencedAt;

    public ConcurrentQueue<TimeSpan> Holds { get; } = new();

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("pro_hype_revision", StringComparison.Ordinal)) fencedAt = Stopwatch.GetTimestamp();
        return ValueTask.FromResult(result);
    }

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (fencedAt != 0) Holds.Enqueue(Stopwatch.GetElapsedTime(fencedAt));
        fencedAt = 0;
        return Task.CompletedTask;
    }
}
