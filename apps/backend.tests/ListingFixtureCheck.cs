using System.Diagnostics;
using System.Text.Json;
using backend.Data;
using ListingCheck;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace backend.tests;

internal sealed record QueryWork(string Path, int LatestReads, int ExcitementReads, int ExcitementGames, int RelationScans,
    int DistinctRelations, IReadOnlyList<string> DuplicateRelations, int ChunkScans, IReadOnlyList<string> NewestSnapshotChunks, double? PlanningMs);

internal sealed record FixtureControls(bool AppFactoryWorkersRemoved, int TimescaleBackgroundWorkers, string ProviderHttp, int ProviderRequestsAttempted);

internal sealed record FixtureDetails(WorkloadDescription Workload, FixtureControls Controls, IReadOnlyDictionary<string, double> Startup,
    IReadOnlyList<QueryWork> QueryWork);

internal static class ListingFixtureCheck
{
    private const string TimingOwner = "ListingFixtureCheck";
    private const string Target = "in-process fixture (loopback)";
    public const string ContainerPrefix = "nailbiter-listing-check-";

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output,
        Func<Uri, CancellationToken, Task>? whileServing = null, CancellationToken cancellationToken = default,
        Action<IServiceCollection>? configureServices = null)
    {
        if (!CheckOptions.TryParse(CheckMode.Fixture, args, out var parsed, out var invalid))
            return await ListingCheckCommand.RejectAsync(output, invalid);
        var options = parsed with { Paths = ListingWorkload.Paths };
        var start = CheckStart.Now();
        var image = ListingWorkload.Image();
        var startup = new Dictionary<string, double>();
        async Task<T> Phase<T>(string name, Func<Task<T>> work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp();
            var result = await FixtureTimings.MeasureAsync(TimingOwner, name, work);
            startup[name] = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1);
            return result;
        }
        Task Step(string name, Func<Task> work) => Phase(name, async () => { await work(); return true; });

        var recorder = new CommandRecorder();
        var server = new SharedPostgreSql(IsolatedPostgreSql.CreateBuilder(image).WithName(ContainerPrefix + Guid.NewGuid().ToString("N"))
            .WithCommand(ListingWorkload.ServerSettings).Build(),
            ListingWorkload.PrepareTemplateAsync);
        var host = new HostedGameFixture(ListingWorkload.Now,
            services =>
            {
                configureServices?.Invoke(services);
                services.AddDbContext<ExcitementDbContext>(o => o.AddInterceptors(recorder)).AddLogging(logging => logging.ClearProviders());
            },
            server, listenOnLoopback: true);
        try
        {
            await Step("container", server.StartContainerAsync);
            await Step("template", server.CreateTemplateAsync);
            await Step("app", host.InitializeAsync);
            var manifest = await Phase("seed", () => ListingWorkload.SeedAsync(host.Services));
            var workload = await Phase("compress-analyze", () => ListingWorkload.CompressAndAnalyzeAsync(host.Services, image));
            var address = host.Client.BaseAddress!;
            using var client = ListingProbe.CreateClient();
            var probe = await ListingProbe.RunAsync(client, address, options, manifest.Check, cancellationToken);
            var queryWork = new List<QueryWork>();
            var failures = new List<Failure>();
            foreach (var path in options.Paths)
            {
                recorder.Commands.Clear();
                using (var response = await host.Client.GetAsync(path, cancellationToken)) response.EnsureSuccessStatusCode();
                queryWork.Add(await CheckQueryWorkAsync(host, path, recorder.Commands.ToList(), manifest, failures));
            }
            if (whileServing is not null) await whileServing(address, cancellationToken);
            var controls = new FixtureControls(true, 0, "rejected", host.RejectedOutboundRequests);
            var report = CheckReport.Create(start, CheckMode.Fixture, options, Target, probe, failures,
                new FixtureDetails(workload, controls, startup, queryWork));
            await report.WriteAsync(output, options.ReportPath);
            return report.ExitCode;
        }
        finally
        {
            await host.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    private static async Task<QueryWork> CheckQueryWorkAsync(HostedGameFixture host, string path, List<CapturedCommand> commands,
        ListingWorkloadManifest manifest, List<Failure> failures)
    {
        var expectedIds = manifest.ExcitementIds(path);
        var latest = commands.Where(c => c.Sql.Contains("odds_snapshots") && !c.Sql.Contains("is_historical_import")).ToList();
        var excitement = commands.Where(c => c.Sql.Contains("odds_snapshots") && c.Sql.Contains("is_historical_import")).ToList();
        var readIds = excitement.SelectMany(c => c.Parameters.Select(p => p.Value).OfType<IEnumerable<string>>().SelectMany(ids => ids)
            .Concat(c.Parameters.Select(p => p.Value).OfType<string>())
            .Concat(manifest.On("/").Select(game => game.Id).Where(id => c.Sql.Contains($"'{id}'", StringComparison.Ordinal)))).ToHashSet();
        var (relations, planningMs) = latest.Count == 1 ? await ExplainAsync(host.Services, latest[0]) : ([], null);
        var duplicates = relations.GroupBy(name => name).Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        var newestChunks = await NewestSnapshotChunksAsync(host.Services, manifest.On(path).Select(game => game.Id).ToArray());
        var unread = newestChunks.Except(relations).ToList();
        var unexpected = readIds.Except(expectedIds).Order().ToList();
        var missing = expectedIds.Except(readIds).Order().ToList();
        (bool Broken, string Rule)[] rules =
        [
            (latest.Count != 1, $"expected 1 latest-odds read, saw {latest.Count}"),
            (duplicates.Count > 0, $"latest-odds plan scans {duplicates.Count} relations more than once, e.g. {string.Join(", ", duplicates.Take(3))}"),
            (newestChunks.Count == 0, "listed games have no newest snapshot chunk to look for in the latest-odds plan"),
            (unread.Count > 0, $"latest-odds plan does not show a scan of {string.Join(", ", unread)}, which holds the newest snapshots"),
            (excitement.Count != (expectedIds.Count > 0 ? 1 : 0), $"expected {(expectedIds.Count > 0 ? 1 : 0)} excitement history read, saw {excitement.Count}"),
            (unexpected.Count > 0, $"excitement read includes games without live or final status: {string.Join(", ", unexpected)}"),
            (missing.Count > 0, $"excitement read misses live or final games: {string.Join(", ", missing)}"),
            (host.RejectedOutboundRequests != 0, $"{host.RejectedOutboundRequests} provider requests attempted")
        ];
        failures.AddRange(rules.Where(rule => rule.Broken).Select(rule => new Failure(FailureKind.QueryWork, path, rule.Rule)));
        return new(path, latest.Count, excitement.Count, readIds.Count, relations.Count, relations.Distinct().Count(), duplicates,
            relations.Count(name => name.Contains("_hyper_", StringComparison.Ordinal)), newestChunks, planningMs);
    }

    private static async Task<List<string>> NewestSnapshotChunksAsync(IServiceProvider services, string[] gameIds)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT DISTINCT chunk.relname FROM (SELECT DISTINCT ON (game_id) tableoid FROM odds_snapshots WHERE game_id = ANY (@ids)
                ORDER BY game_id, timestamp DESC, id DESC) newest JOIN pg_class chunk ON chunk.oid = newest.tableoid ORDER BY 1
            """, (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("ids", gameIds);
        var chunks = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) chunks.Add(reader.GetString(0));
        return chunks;
    }

    private static async Task<(List<string> Relations, double? PlanningMs)> ExplainAsync(IServiceProvider services, CapturedCommand captured)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("EXPLAIN (FORMAT JSON, SUMMARY true) " + captured.Sql,
            (NpgsqlConnection)db.Database.GetDbConnection());
        foreach (var (name, value) in captured.Parameters) command.Parameters.Add(new NpgsqlParameter(name, value));
        using var plan = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
        var root = plan.RootElement[0];
        var relations = QueryPlans.Descendants(root.GetProperty("Plan"))
            .Where(node => node.TryGetProperty("Relation Name", out _)).Select(node => node.GetProperty("Relation Name").GetString()!).ToList();
        return (relations, root.TryGetProperty("Planning Time", out var planning) ? planning.GetDouble() : null);
    }
}

internal static class ListingCheckEntryPoint
{
    private const string Usage = "usage: dotnet run --project apps/backend.tests -c Release -- fixture --ttfb-budget-ms <ms> "
        + "--completion-budget-ms <ms> [--samples 1-100] [--warmup 0-20] [--request-timeout-ms <ms>] [--report <file>]";

    private static async Task<int> Main(string[] args)
    {
        if (args is not ["fixture", .. var rest])
        {
            await Console.Error.WriteLineAsync(Usage);
            return (int)FailureKind.InvalidConfig;
        }
        var arguments = rest.Select((arg, i) => i > 0 && rest[i - 1] == "--report" && !arg.StartsWith("--", StringComparison.Ordinal)
            ? Path.GetFullPath(arg) : arg).ToArray();
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        return await ListingCheckCommand.RunUntilInterruptedAsync(token => ListingFixtureCheck.RunAsync(arguments, Console.Out, cancellationToken: token));
    }
}
