using System.Text.Json;
using AngleSharp.Html.Parser;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FSharp.Collections;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace backend.tests;

public sealed class ListingTimescale : IAsyncLifetime
{
    internal SharedPostgreSql Server { get; } = new(
        IsolatedPostgreSql.CreateBuilder(ListingWorkload.Image()).WithCommand(ListingWorkload.ServerSettings).Build(),
        ListingWorkload.PrepareTemplateAsync);

    public Task InitializeAsync() => Server.InitializeAsync();
    public Task DisposeAsync() => Server.DisposeAsync();
}

internal sealed record LatestOddsPlan(string Path, int Chunks, int NewestChunks, bool OnlyNewestChunks, double PlanningMs, double ExecutionMs,
    long SharedHit, long SharedRead);

public sealed class LatestOddsHistoryTests(ListingTimescale timescale, ITestOutputHelper output) : IClassFixture<ListingTimescale>
{
    private static readonly DateTime Now = ListingWorkload.Now.UtcDateTime;

    [Fact]
    public async Task Latest_odds_read_plans_only_the_newest_chunks_for_short_and_long_histories()
    {
        var shortHistory = await PlansAsync(7);
        var longHistory = await PlansAsync(56);

        Assert.All(shortHistory.Concat(longHistory), plan => Assert.True(plan.OnlyNewestChunks,
            $"{plan.Path} plans {plan.Chunks} chunks; its listed games' newest odds are in {plan.NewestChunks}"));
        Assert.Equal(shortHistory.Select(plan => (plan.Path, plan.Chunks)), longHistory.Select(plan => (plan.Path, plan.Chunks)));
    }

    [Fact]
    public async Task Seeding_writes_newest_odds_once_per_game_and_matches_what_the_trigger_builds()
    {
        var host = new HostedGameFixture(ListingWorkload.Now, null, timescale.Server);
        await host.InitializeAsync();
        try
        {
            await ListingWorkload.SeedAsync(host.Services, historyDays: 1);
            string connectionString;
            using (var scope = host.Services.CreateScope())
                connectionString = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.GetConnectionString()!;
            NpgsqlConnection.ClearPool(new NpgsqlConnection(connectionString));
            var writes = await NewestOddsWritesAsync(connectionString);
            var seeded = await NewestOddsAsync(host.Services);

            await ExecuteAsync(host.Services, """
                CREATE TEMP TABLE replay AS SELECT * FROM odds_snapshots;
                TRUNCATE odds_snapshots, newest_odds;
                INSERT INTO odds_snapshots SELECT * FROM replay;
                """);
            var triggered = await NewestOddsAsync(host.Services);

            Assert.True(writes == seeded.Count,
                $"the seed wrote newest_odds {writes} times for {seeded.Count} games; each snapshot ran the per-row trigger");
            Assert.Equal(triggered, seeded);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task Listed_games_keep_their_newest_odds_wherever_they_sit_in_history()
    {
        var host = new HostedGameFixture(ListingWorkload.Now, null, timescale.Server);
        await host.InitializeAsync();
        try
        {
            await ListingWorkload.SeedAsync(host.Services);
            await ExecuteAsync(host.Services, "DROP TRIGGER newest_odds_track ON odds_snapshots; DROP TABLE newest_odds");
            await host.SeedAsync(Edge("edge-stuck", "in_progress", Now.AddDays(-40)), Quotes("edge-stuck", Now.AddDays(-40), .62));
            await host.SeedAsync(Edge("edge-postponed", "scheduled", Now.AddHours(2)), Quotes("edge-postponed", Now.AddDays(-3), .64));
            await host.SeedAsync(Edge("edge-backfilled", "scheduled", Now.AddHours(2)), []);
            await host.SeedAsync(Edge("edge-recovered", "scheduled", Now.AddHours(2)),
                [.. Quotes("edge-recovered", Now.AddDays(-12), .68), .. Quotes("edge-recovered", Now.AddDays(-11), .9)]);
            await host.SeedAsync(Edge("edge-replaced", "scheduled", Now.AddHours(2)), Quotes("edge-replaced", Now.AddDays(-5), .9));
            await ExecuteAsync(host.Services, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "042_NewestOdds.sql")));
            await ListingWorkload.CompressAndAnalyzeAsync(host.Services, ListingWorkload.Image());
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
                db.OddsSnapshots.AddRange([.. Quotes("edge-backfilled", Now.AddDays(-20), .66), .. Quotes("edge-postponed", Now.AddDays(-30), .1)]);
                await db.SaveChangesAsync();
                await db.OddsSnapshots.Where(o => o.GameId == "edge-recovered" && o.Timestamp >= Now.AddDays(-11)).ExecuteDeleteAsync();
                await db.OddsSnapshots.Where(o => o.GameId == "edge-replaced").ExecuteDeleteAsync();
                db.OddsSnapshots.AddRange(Quotes("edge-replaced", Now.AddDays(-25), .7));
                await db.SaveChangesAsync();
                Assert.Equal(4, await db.Database.SqlQueryRaw<int>("""
                    SELECT count(DISTINCT chunk.relname)::int AS "Value" FROM odds_snapshots o JOIN pg_class chunk ON chunk.oid = o.tableoid
                    JOIN timescaledb_information.chunks info ON info.chunk_name = chunk.relname
                    WHERE o.game_id LIKE 'edge-%' AND info.is_compressed
                    """).SingleAsync());
            }

            foreach (var path in new[] { "/", "/nhl?date=2026-10-13" })
            {
                using var page = new HtmlParser().ParseDocument(await host.Client.GetStringAsync(path));
                foreach (var (id, percent) in new[] { ("edge-stuck", 62), ("edge-postponed", 64), ("edge-backfilled", 66), ("edge-recovered", 68), ("edge-replaced", 70) })
                    Assert.Contains($"HOM {percent}%", page.QuerySelector($"#probbar-{id}")?.TextContent ?? $"{path} shows no odds for {id}");
            }

            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
                var ids = await db.Games.Select(game => game.Id).ToListAsync();
                var newest = await db.OddsSnapshots
                    .FromSqlRaw("SELECT DISTINCT ON (game_id) * FROM odds_snapshots ORDER BY game_id, timestamp DESC, id DESC")
                    .AsNoTracking().ToDictionaryAsync(snapshot => snapshot.GameId, snapshot => snapshot.Id);
                var latest = await scope.ServiceProvider.GetRequiredService<GameDataService>()
                    .GetLatestOddsForGamesAsync(ListModule.OfSeq(ids), CancellationToken.None);
                Assert.Equal(newest.OrderBy(entry => entry.Key, StringComparer.Ordinal),
                    latest.Select(entry => KeyValuePair.Create(entry.Key, entry.Value.Id)).OrderBy(entry => entry.Key, StringComparer.Ordinal));
            }
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("an older delete, then a newest delete", "DELETE FROM odds_snapshots WHERE game_id = 'race' AND timestamp = @older",
        "DELETE FROM odds_snapshots WHERE game_id = 'race' AND timestamp = @newest")]
    [InlineData("a newest delete, then an older delete", "DELETE FROM odds_snapshots WHERE game_id = 'race' AND timestamp = @newest",
        "DELETE FROM odds_snapshots WHERE game_id = 'race' AND timestamp = @older")]
    [InlineData("an older move, then a newest delete", "UPDATE odds_snapshots SET game_id = 'race-moved' WHERE game_id = 'race' AND timestamp = @older",
        "DELETE FROM odds_snapshots WHERE game_id = 'race' AND timestamp = @newest")]
    [InlineData("a newer insert, then a newest delete", """
        INSERT INTO odds_snapshots (game_id, timestamp, home_moneyline, away_moneyline, home_win_probability, away_win_probability, source, is_historical)
        VALUES ('race', @newer, -100, -100, .5, .5, 'Kalshi', false)
        """, "DELETE FROM odds_snapshots WHERE game_id = 'race' AND timestamp = @newest")]
    public async Task Concurrent_odds_changes_to_one_game_keep_its_newest_odds(string interleaving, string first, string second)
    {
        var host = new HostedGameFixture(ListingWorkload.Now, null, timescale.Server);
        await host.InitializeAsync();
        try
        {
            await host.SeedAsync(Edge("race-moved", "final", Now.AddDays(-4)), []);
            await host.SeedAsync(Edge("race", "final", Now.AddDays(-1)),
                new[] { -3, -2, -1 }.Select(days => Quote("race", Now.AddDays(days))));
            string connectionString;
            using (var scope = host.Services.CreateScope())
                connectionString = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.GetConnectionString()!;

            await using var firstSession = await BeginAsync(connectionString);
            await using var secondSession = await BeginAsync(connectionString);
            await RaceCommand(firstSession, first).ExecuteNonQueryAsync();
            var secondChange = RaceCommand(secondSession, second).ExecuteNonQueryAsync();
            await UntilFinishedOrWaitingOnALockAsync(connectionString, secondSession.Connection!.ProcessID, secondChange);
            await firstSession.CommitAsync();
            await secondChange;
            await secondSession.CommitAsync();

            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
                var ids = new[] { "race", "race-moved" };
                var newest = await db.OddsSnapshots
                    .FromSqlRaw("SELECT DISTINCT ON (game_id) * FROM odds_snapshots WHERE game_id = ANY ({0}) ORDER BY game_id, timestamp DESC, id DESC", new object[] { ids })
                    .AsNoTracking().ToDictionaryAsync(snapshot => snapshot.GameId, snapshot => (snapshot.Id, snapshot.Timestamp));
                var pointers = await db.Database.SqlQueryRaw<string>(
                    "SELECT game_id || ' at ' || timestamp AS \"Value\" FROM newest_odds WHERE game_id = ANY ({0}) ORDER BY game_id", new object[] { ids }).ToListAsync();
                var latest = await scope.ServiceProvider.GetRequiredService<GameDataService>()
                    .GetLatestOddsForGamesAsync(ListModule.OfSeq(ids), CancellationToken.None);
                Assert.True(newest.OrderBy(entry => entry.Key).Select(entry => (entry.Key, entry.Value.Id))
                        .SequenceEqual(latest.OrderBy(entry => entry.Key).Select(entry => (entry.Key, entry.Value.Id))),
                    $"after {interleaving}, newest snapshots are [{string.Join(", ", newest.Select(entry => $"{entry.Key} at {entry.Value.Timestamp:O}"))}] "
                    + $"but newest_odds holds [{string.Join(", ", pointers)}] and the read returned [{string.Join(", ", latest.Select(entry => entry.Key))}]");
            }
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private static async Task<NpgsqlTransaction> BeginAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return await connection.BeginTransactionAsync();
    }

    private static NpgsqlCommand RaceCommand(NpgsqlTransaction session, string sql) => new(sql, session.Connection, session)
    {
        Parameters =
        {
            new("older", Now.AddDays(-2)),
            new("newest", Now.AddDays(-1)),
            new("newer", Now),
        },
    };

    private static async Task UntilFinishedOrWaitingOnALockAsync(string connectionString, int processId, Task change)
    {
        await using var observer = new NpgsqlConnection(connectionString);
        await observer.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!change.IsCompleted)
        {
            await using var waiting = new NpgsqlCommand("SELECT wait_event_type = 'Lock' FROM pg_stat_activity WHERE pid = @pid", observer)
                { Parameters = { new("pid", processId) } };
            if (await waiting.ExecuteScalarAsync() is true) return;
            Assert.True(DateTime.UtcNow < deadline, "the second session neither finished nor waited on a lock");
            await Task.Delay(20);
        }
    }

    private static OddsSnapshot Quote(string id, DateTime at) => new()
    {
        GameId = id, Timestamp = at, HomeWinProbability = .5, AwayWinProbability = .5, HomeMoneyline = -100, AwayMoneyline = -100, Source = "Kalshi"
    };

    private static Game Edge(string id, string status, DateTime start) => new()
    {
        Id = id, Sport = "nhl", Status = status, SeasonYear = 2027, SeasonType = 2, ScheduledStart = start,
        HomeTeam = "Home", AwayTeam = "Away", HomeTeamAbbreviation = "HOM", AwayTeamAbbreviation = "AWY"
    };

    private static IEnumerable<OddsSnapshot> Quotes(string id, DateTime start, double last) =>
        new[] { .5, .5, last }.Select((probability, minute) => new OddsSnapshot
        {
            GameId = id, Timestamp = start.AddMinutes(minute), HomeWinProbability = probability, AwayWinProbability = 1 - probability,
            HomeMoneyline = -100, AwayMoneyline = -100, Source = "Kalshi"
        });

    private static async Task<List<string>> NewestOddsAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.SqlQueryRaw<string>(
            "SELECT game_id || ' at ' || timestamp AS \"Value\" FROM newest_odds ORDER BY game_id").ToListAsync();
    }

    private static async Task<long> NewestOddsWritesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            await using var writes = new NpgsqlCommand(
                "SELECT n_tup_ins + n_tup_upd FROM pg_stat_user_tables WHERE relname = 'newest_odds'", connection);
            if (await writes.ExecuteScalarAsync() is long count and > 0) return count;
            Assert.True(DateTime.UtcNow < deadline, "pg_stat_user_tables shows no newest_odds writes from the seed after 30 s");
            await Task.Delay(100);
        }
    }

    private static async Task ExecuteAsync(IServiceProvider services, string sql)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<List<LatestOddsPlan>> PlansAsync(int historyDays)
    {
        var recorder = new CommandRecorder();
        var host = new HostedGameFixture(ListingWorkload.Now,
            services => services.AddDbContext<ExcitementDbContext>(options => options.AddInterceptors(recorder)), timescale.Server);
        await host.InitializeAsync();
        try
        {
            var manifest = await ListingWorkload.SeedAsync(host.Services, historyDays);
            var workload = await ListingWorkload.CompressAndAnalyzeAsync(host.Services, ListingWorkload.Image());
            var plans = new List<LatestOddsPlan>();
            foreach (var path in ListingWorkload.Paths)
            {
                recorder.Commands.Clear();
                var body = await host.Client.GetStringAsync(path);
                Assert.Empty(manifest.Check(path, body));
                var latest = Assert.Single(recorder.Commands, c => c.Sql.Contains("odds_snapshots") && !c.Sql.Contains("is_historical_import"));
                var plan = await ExplainAsync(host.Services, path, latest, manifest.On(path).Select(game => game.Id).ToArray());
                output.WriteLine($"history {historyDays} days, {workload.Chunks} chunks ({workload.CompressedChunks} compressed), "
                    + $"{workload.Snapshots} snapshots: {plan}");
                plans.Add(plan);
            }
            return plans;
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private static async Task<LatestOddsPlan> ExplainAsync(IServiceProvider services, string path, CapturedCommand captured, string[] listed)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + captured.Sql,
            (NpgsqlConnection)db.Database.GetDbConnection());
        foreach (var (name, value) in captured.Parameters) command.Parameters.Add(new NpgsqlParameter(name, value));
        using var explained = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
        var root = explained.RootElement[0];
        var plan = root.GetProperty("Plan");
        var oddsChunks = await db.Database.SqlQueryRaw<string>(
            "SELECT chunk_name AS \"Value\" FROM timescaledb_information.chunks WHERE hypertable_name = 'odds_snapshots'").ToListAsync();
        var planned = QueryPlans.Descendants(plan)
            .Select(node => node.TryGetProperty("Relation Name", out var name) ? name.GetString()! : "")
            .Where(oddsChunks.Contains).ToHashSet();
        var newest = await db.Database.SqlQueryRaw<string>("""
            SELECT DISTINCT chunk.relname AS "Value" FROM (SELECT DISTINCT ON (game_id) tableoid FROM odds_snapshots WHERE game_id = ANY ({0})
                ORDER BY game_id, timestamp DESC, id DESC) newest JOIN pg_class chunk ON chunk.oid = newest.tableoid
            """, new object[] { listed }).ToListAsync();
        return new(path, planned.Count, newest.Count, planned.SetEquals(newest), root.GetProperty("Planning Time").GetDouble(),
            root.GetProperty("Execution Time").GetDouble(), plan.GetProperty("Shared Hit Blocks").GetInt64(), plan.GetProperty("Shared Read Blocks").GetInt64());
    }
}
