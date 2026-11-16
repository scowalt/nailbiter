using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace backend.tests;

public sealed class ListingServerTimeProbe(ITestOutputHelper output)
{
    internal const string RoundsVariable = "NAILBITER_LISTING_PROBE_ROUNDS";

    [ListingServerTimeFact]
    public async Task Measure_server_time_by_phase_for_each_listing_path()
    {
        var rounds = int.Parse(Environment.GetEnvironmentVariable(RoundsVariable)!, CultureInfo.InvariantCulture);
        var timer = new CommandTimer();
        var server = new SharedPostgreSql(IsolatedPostgreSql.CreateBuilder(ListingWorkload.Image())
            .WithCommand(ListingWorkload.ServerSettings).Build(), ListingWorkload.PrepareTemplateAsync);
        await server.InitializeAsync();
        var host = new HostedGameFixture(ListingWorkload.Now, services => services
            .AddDbContext<ExcitementDbContext>(o => o.AddInterceptors(timer)).AddLogging(logging => logging.ClearProviders()), server);
        await host.InitializeAsync();
        try
        {
            var manifest = await ListingWorkload.SeedAsync(host.Services);
            await ListingWorkload.CompressAndAnalyzeAsync(host.Services, ListingWorkload.Image());
            var dump = Environment.GetEnvironmentVariable("NAILBITER_LISTING_PROBE_DUMP");
            foreach (var path in ListingWorkload.Paths)
            {
                var body = await host.Client.GetStringAsync(path);
                Assert.Empty(manifest.Check(path, body));
                if (dump is not null)
                    await File.WriteAllTextAsync(Path.Combine(dump, Regex.Replace(path, "[^a-z0-9]+", "_") + ".html"), body);
            }
            foreach (var path in ListingWorkload.Paths)
            {
                var samples = new List<(double Total, double Db, int Commands, int Commits, Dictionary<string, (int Count, double Ms)> Shapes)>();
                for (var round = 0; round < rounds; round++)
                {
                    timer.Reset();
                    var started = Stopwatch.GetTimestamp();
                    using (var response = await host.Client.GetAsync(path)) response.EnsureSuccessStatusCode();
                    var total = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    var entries = timer.Entries.ToList();
                    samples.Add((total, entries.Sum(e => e.Ms), entries.Count(e => e.Kind != "commit"), entries.Count(e => e.Kind == "commit"),
                        entries.GroupBy(e => e.Shape).ToDictionary(g => g.Key, g => (g.Count(), g.Sum(e => e.Ms)))));
                }
                double Median(IEnumerable<double> values) { var v = values.Order().ToArray(); return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2; }
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{path}: rounds={rounds} totalMs med={Median(samples.Select(s => s.Total)):F1} dbMs med={Median(samples.Select(s => s.Db)):F1} "
                    + $"appMs med={Median(samples.Select(s => s.Total - s.Db)):F1} commands={samples[^1].Commands} commits={samples[^1].Commits}"));
                foreach (var shape in samples[^1].Shapes.Keys.OrderByDescending(k => Median(samples.Select(s => s.Shapes.GetValueOrDefault(k).Ms))))
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"    {samples[^1].Shapes[shape].Count,4} x {Median(samples.Select(s => s.Shapes.GetValueOrDefault(shape).Ms)),8:F1} ms  {shape}"));
                if (Environment.GetEnvironmentVariable("NAILBITER_LISTING_PROBE_EXPLAIN") is null) continue;
                foreach (var (shape, (sql, parameters)) in timer.Samples.Where(s => s.Key.StartsWith("SELECT", StringComparison.Ordinal)))
                {
                    using var scope = host.Services.CreateScope();
                    var connection = (NpgsqlConnection)scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.GetDbConnection();
                    await connection.OpenAsync();
                    await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + sql, connection);
                    explain.Parameters.AddRange(parameters.Select(p => p.Clone()).ToArray());
                    var plan = new List<string>();
                    await using (var reader = await explain.ExecuteReaderAsync())
                        while (await reader.ReadAsync()) plan.Add(reader.GetString(0));
                    output.WriteLine($"  EXPLAIN {shape}");
                    foreach (var line in plan) output.WriteLine("    | " + line);
                }
            }
        }
        finally
        {
            await host.DisposeAsync();
            await server.DisposeAsync();
        }
    }
}

public sealed class ListingServerTimeFactAttribute : FactAttribute
{
    public ListingServerTimeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(ListingServerTimeProbe.RoundsVariable) is null)
            Skip = "Measurement for issue #200; set NAILBITER_LISTING_PROBE_ROUNDS=10 to run it";
    }
}

internal sealed class CommandTimer : DbCommandInterceptor, IDbTransactionInterceptor
{
    private readonly ConcurrentDictionary<DbCommand, long> started = new();
    private readonly ConcurrentDictionary<DbTransaction, long> committing = new();

    public ConcurrentQueue<(string Kind, string Shape, double Ms)> Entries { get; private set; } = new();

    public ConcurrentDictionary<string, (string Sql, NpgsqlParameter[] Parameters)> Samples { get; private set; } = new();

    public void Reset()
    {
        Entries = new();
        Samples = new();
    }

    private static string Shape(string sql)
    {
        var text = Regex.Replace(sql, @"\s+", " ").Trim();
        text = Regex.Replace(text, @"@\w+|\$\d+|'[^']*'", "?");
        return text.Length > 150 ? text[..150] : text;
    }

    private void Start(DbCommand command)
    {
        Samples.TryAdd(Shape(command.CommandText), (command.CommandText, command.Parameters.Cast<NpgsqlParameter>().Select(p => p.Clone()).ToArray()));
        started[command] = Stopwatch.GetTimestamp();
    }

    private void Stop(DbCommand command, string kind)
    {
        if (started.TryRemove(command, out var at))
            Entries.Enqueue((kind, Shape(command.CommandText), Stopwatch.GetElapsedTime(at).TotalMilliseconds));
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Start(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult DataReaderClosing(DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
    {
        Stop(command, "reader");
        return result;
    }

    public override ValueTask<InterceptionResult> DataReaderClosingAsync(DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
    {
        Stop(command, "reader");
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
    {
        Stop(command, "reader");
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Start(command);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Stop(command, "nonquery");
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Start(command);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        Stop(command, "scalar");
        return ValueTask.FromResult(result);
    }

    public ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
        InterceptionResult result, CancellationToken cancellationToken = default)
    {
        committing[transaction] = Stopwatch.GetTimestamp();
        return ValueTask.FromResult(result);
    }

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (committing.TryRemove(transaction, out var at))
            Entries.Enqueue(("commit", "COMMIT", Stopwatch.GetElapsedTime(at).TotalMilliseconds));
        return Task.CompletedTask;
    }
}
