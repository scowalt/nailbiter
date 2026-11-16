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

    private static Game Game(string id) => new()
    {
        Id = id, Sport = "nhl", Status = "in_progress", SeasonYear = 2027, SeasonType = 2, ScheduledStart = Now.AddHours(-1),
        HomeTeam = "Home", AwayTeam = "Away", HomeTeamAbbreviation = "HOM", AwayTeamAbbreviation = "AWY"
    };

    private static OddsSnapshot Quote(string id, DateTime at, double home) => new()
    {
        GameId = id, Timestamp = at, HomeWinProbability = home, AwayWinProbability = 1 - home,
        HomeMoneyline = -100, AwayMoneyline = -100, Source = "Kalshi"
    };
}
