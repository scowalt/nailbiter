using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Microsoft.FSharp.Collections;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace backend.tests;

public sealed class MigrationReplayTests
{
    private static readonly DateTime Now = ListingWorkload.Now.UtcDateTime;
    private static readonly string MigrationsDirectory = Path.Combine(AppContext.BaseDirectory, "Migrations");
    private static readonly string[] Migrations =
        Directory.GetFiles(MigrationsDirectory, "*.sql").Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray();
    private static readonly string[] BeforeWidening = Migrations.Where(name => string.CompareOrdinal(name, "044") < 0).ToArray();

    [Fact]
    public async Task Replaying_every_migration_retires_latest_odds_and_keeps_the_newest_odds_listing_read()
    {
        await using var container = await StartAsync();
        foreach (var deploy in new[] { 1, 2 })
            await DeployAsync(container, deploy, Migrations);

        await using var db = Context(container);
        db.Games.AddRange(Game("tied"), Game("replaced"), Game("quiet"));
        db.OddsSnapshots.AddRange(
            Quote("tied", Now.AddMinutes(-2), .40), Quote("tied", Now.AddMinutes(-1), .55), Quote("tied", Now.AddMinutes(-1), .61),
            Quote("replaced", Now.AddMinutes(-3), .72), Quote("replaced", Now.AddMinutes(-1), .30));
        await db.SaveChangesAsync();
        await db.OddsSnapshots.Where(o => o.GameId == "replaced" && o.HomeWinProbability == .30).ExecuteDeleteAsync();

        var expected = await db.OddsSnapshots
            .FromSqlRaw("SELECT DISTINCT ON (game_id) * FROM odds_snapshots ORDER BY game_id, timestamp DESC, id DESC")
            .AsNoTracking().ToDictionaryAsync(snapshot => snapshot.GameId, snapshot => (snapshot.Id, snapshot.HomeWinProbability));
        var listed = await new GameDataService(db, new FakeTimeProvider(ListingWorkload.Now))
            .GetLatestOddsForGamesAsync(ListModule.OfSeq(new[] { "tied", "replaced", "quiet" }), CancellationToken.None);
        Assert.Equal(new Dictionary<string, double> { ["tied"] = .61, ["replaced"] = .72 },
            expected.ToDictionary(entry => entry.Key, entry => entry.Value.HomeWinProbability));
        Assert.Equal(expected.OrderBy(entry => entry.Key, StringComparer.Ordinal),
            listed.Select(entry => KeyValuePair.Create(entry.Key, (entry.Value.Id, entry.Value.HomeWinProbability))).OrderBy(entry => entry.Key, StringComparer.Ordinal));

        Assert.Equal("r", await ScalarAsync(db, "SELECT relkind::text AS \"Value\" FROM pg_class WHERE oid = to_regclass('newest_odds')"));
        Assert.Equal("O", await ScalarAsync(db,
            "SELECT tgenabled::text AS \"Value\" FROM pg_trigger WHERE tgrelid = 'odds_snapshots'::regclass AND tgname = 'newest_odds_track'"));
        Assert.Null(await ScalarAsync(db, "SELECT to_regclass('latest_odds')::text AS \"Value\""));
    }

    [Fact]
    public async Task Replaying_every_migration_twice_on_a_fresh_database_widens_games_status_and_keeps_probability_changes()
    {
        await using var container = await StartAsync();
        foreach (var deploy in new[] { 1, 2 })
            await DeployAsync(container, deploy, Migrations);

        await using var db = Context(container);
        Assert.Equal("character varying(30)", await StatusTypeAsync(db));
        Assert.Equal("game_id,timestamp,home_win_probability,away_win_probability,home_prob_change,period,game_clock,draw_win_probability",
            await ScalarAsync(db, "SELECT string_agg(attname, ',' ORDER BY attnum) AS \"Value\" FROM pg_attribute WHERE attrelid = to_regclass('probability_changes')"));
        Assert.Null(await ScalarAsync(db, "SELECT to_regclass('latest_odds')::text AS \"Value\""));
        db.Games.Add(Game("long-status", new string('s', 30)));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Replaying_over_compressed_odds_chunks_widens_games_status_from_20_and_keeps_every_snapshot_compressed()
    {
        await using var container = await StartAsync();
        foreach (var deploy in new[] { 1, 2 })
            await DeployAsync(container, deploy, BeforeWidening);
        await using var db = Context(container);
        db.Games.AddRange(Game("old"), Game("recent"));
        db.OddsSnapshots.AddRange(Enumerable.Range(0, 96).Select(hour => Quote(hour % 2 == 0 ? "old" : "recent", Now.AddHours(-hour), .5)));
        await db.SaveChangesAsync();
        await ScalarAsync(db, "SELECT count(compress_chunk(chunk))::text AS \"Value\" FROM show_chunks('odds_snapshots') chunk");
        var compressed = "SELECT count(*)::text AS \"Value\" FROM timescaledb_information.chunks WHERE hypertable_name = 'odds_snapshots' AND is_compressed";
        Assert.Equal("character varying(20)", await StatusTypeAsync(db));
        Assert.Equal("4", await ScalarAsync(db, compressed));

        foreach (var deploy in new[] { 3, 4 })
            await DeployAsync(container, deploy, Migrations);

        Assert.Equal("character varying(30)", await StatusTypeAsync(db));
        Assert.Equal("4", await ScalarAsync(db, compressed));
        Assert.Equal("96", await ScalarAsync(db, "SELECT count(*)::text AS \"Value\" FROM odds_snapshots"));
        Assert.NotNull(await ScalarAsync(db, "SELECT to_regclass('probability_changes')::text AS \"Value\""));
        db.Games.Add(Game("long-status", new string('s', 30)));
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("varchar(40)", "character varying(40)")]
    [InlineData("text", "text")]
    public async Task Replaying_keeps_a_games_status_already_wider_than_30(string wider, string expected)
    {
        await using var container = await StartAsync();
        foreach (var deploy in new[] { 1, 2 })
            await DeployAsync(container, deploy, BeforeWidening);
        await using var db = Context(container);
        await db.Database.ExecuteSqlRawAsync($"ALTER TABLE games ALTER COLUMN status TYPE {wider}");

        foreach (var deploy in new[] { 3, 4 })
            await DeployAsync(container, deploy, Migrations);

        Assert.Equal(expected, await StatusTypeAsync(db));
    }

    private static async Task<PostgreSqlContainer> StartAsync()
    {
        var container = IsolatedPostgreSql.CreateBuilder(ListingWorkload.Image()).WithCommand(ListingWorkload.ServerSettings).Build();
        await container.StartAsync();
        foreach (var name in Migrations)
            await container.CopyAsync(await File.ReadAllBytesAsync(Path.Combine(MigrationsDirectory, name)), "/migrations/" + name);
        return container;
    }

    private static async Task DeployAsync(PostgreSqlContainer container, int deploy, IEnumerable<string> names)
    {
        var target = new NpgsqlConnectionStringBuilder(container.GetConnectionString());
        foreach (var name in names)
        {
            var legacy = string.CompareOrdinal(name, "021") < 0;
            var result = await container.ExecAsync(
                ["psql", "-q", "-v", $"ON_ERROR_STOP={(legacy ? 0 : 1)}", "-U", target.Username!, "-d", target.Database!, "-f", "/migrations/" + name]);
            Assert.True(legacy || result.ExitCode == 0, $"deploy {deploy}: {name} exited {result.ExitCode}: {result.Stderr}");
        }
    }

    private static ExcitementDbContext Context(PostgreSqlContainer container) =>
        new(new DbContextOptionsBuilder<ExcitementDbContext>().UseNpgsql(container.GetConnectionString()).Options);

    private static Task<string?> ScalarAsync(ExcitementDbContext db, string sql) => db.Database.SqlQueryRaw<string?>(sql).SingleOrDefaultAsync();

    private static Task<string?> StatusTypeAsync(ExcitementDbContext db) =>
        ScalarAsync(db, "SELECT format_type(atttypid, atttypmod) AS \"Value\" FROM pg_attribute WHERE attrelid = 'games'::regclass AND attname = 'status'");

    private static Game Game(string id, string status = "in_progress") => new()
    {
        Id = id, Sport = "nhl", Status = status, SeasonYear = 2027, SeasonType = 2, ScheduledStart = Now.AddHours(-1),
        HomeTeam = "Home", AwayTeam = "Away", HomeTeamAbbreviation = "HOM", AwayTeamAbbreviation = "AWY"
    };

    private static OddsSnapshot Quote(string id, DateTime at, double home) => new()
    {
        GameId = id, Timestamp = at, HomeWinProbability = home, AwayWinProbability = 1 - home,
        HomeMoneyline = -100, AwayMoneyline = -100, Source = "Kalshi"
    };
}
