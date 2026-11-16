using System.Globalization;
using System.Text.Json;
using backend.Data;
using backend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace backend.tests;

public sealed class ProHypeEvidenceReadTests : IClassFixture<SharedPostgreSql>, IAsyncLifetime
{
    private const string Listing = "/nhl?date=2026-10-06";
    private readonly CommandRecorder recorder = new();
    private readonly HostedGameFixture host;
    private readonly ITestOutputHelper output;

    public ProHypeEvidenceReadTests(SharedPostgreSql server, ITestOutputHelper output)
    {
        this.output = output;
        host = new HostedGameFixture(new DateTimeOffset(ProHypeTests.Now), services =>
            services.AddDbContext<ExcitementDbContext>(options => options.AddInterceptors(recorder)), server);
    }

    public async Task InitializeAsync()
    {
        await host.InitializeAsync();
        await Change(async db => await db.Database.ExecuteSqlRawAsync(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "035_ProHypeLifecycle.sql"))));
    }

    public Task DisposeAsync() => host.DisposeAsync();

    [Fact]
    public async Task Listing_a_new_game_beside_saved_games_reads_only_evidence_changed_since_each_game_was_resolved()
    {
        const int refreshes = 200;
        var resolved = await SeedSavedSlateAsync(games: 3, refreshes: refreshes, laterTeam: 9);

        var savedOnly = await ListAndReadAsync();
        Assert.Equal(0, savedOnly.RowsRead);
        Assert.Empty(savedOnly.Returned);

        await SeedGameAsync(9, ratings: false);
        var withNewGame = await ListAndReadAsync();
        long[] newGameChanges;
        await using (var db = Context())
            newGameChanges = await db.ProHypeEvidenceChanges.Where(c => c.Revision > resolved && c.Kind == "games")
                .Select(c => c.Revision).OrderBy(r => r).ToArrayAsync();
        Assert.Equal(newGameChanges, withNewGame.Returned);
        Assert.InRange(withNewGame.RowsRead, 0, refreshes - 1);
    }

    private async Task<(long RowsRead, long[] Returned)> ListAndReadAsync()
    {
        recorder.Commands.Clear();
        (await host.Client.GetAsync(Listing)).EnsureSuccessStatusCode();
        var read = Assert.Single(recorder.Commands, c => c.Sql.Contains("pro_hype_evidence_changes"));
        await using var db = Context();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using (var analyze = new NpgsqlCommand("ANALYZE pro_hype_evidence_changes", connection))
            await analyze.ExecuteNonQueryAsync();
        await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, FORMAT JSON) " + read.Sql, connection);
        explain.Parameters.AddRange(read.Parameters.Select(p => new NpgsqlParameter(p.Name, p.Value)).ToArray());
        using var plan = JsonDocument.Parse((string)(await explain.ExecuteScalarAsync())!);
        var rowsRead = RowsRead(plan.RootElement[0].GetProperty("Plan"));
        await using var select = new NpgsqlCommand("SELECT q.revision FROM (" + read.Sql + ") AS q ORDER BY q.revision", connection);
        select.Parameters.AddRange(read.Parameters.Select(p => new NpgsqlParameter(p.Name, p.Value)).ToArray());
        var returned = new List<long>();
        await using (var reader = await select.ExecuteReaderAsync())
            while (await reader.ReadAsync()) returned.Add(reader.GetInt64(0));
        output.WriteLine($"{rowsRead} change rows read, {returned.Count} returned");
        return (rowsRead, returned.ToArray());
    }

    private static long RowsRead(JsonElement node)
    {
        if (node.TryGetProperty("Plans", out var children)) return children.EnumerateArray().Sum(RowsRead);
        var table = node.TryGetProperty("Relation Name", out var relation) && relation.GetString() == "pro_hype_evidence_changes"
            || node.TryGetProperty("Index Name", out var index) && index.GetString()!.Contains("pro_hype");
        if (!table) return 0;
        var removed = node.TryGetProperty("Rows Removed by Filter", out var filtered) ? filtered.GetInt64() : 0;
        return (node.GetProperty("Actual Rows").GetInt64() + removed) * node.GetProperty("Actual Loops").GetInt64();
    }

    [EvidenceReadProbeFact]
    public async Task Measure_the_evidence_change_read_with_and_without_a_never_listed_game()
    {
        var days = int.Parse(Environment.GetEnvironmentVariable(EvidenceReadProbeFactAttribute.DaysVariable)!, CultureInfo.InvariantCulture);
        var resolved = await SeedSavedSlateAsync(games: 8, refreshes: days * 288, laterTeam: 16);
        await using (var db = Context())
            output.WriteLine($"history: {days} days x 288 Kalshi refreshes, {await db.ProHypeEvidenceChanges.CountAsync()} change rows, saved at revision {resolved}");
        await ExplainListingAsync("saved games only");
        await SeedGameAsync(16, ratings: false);
        await ExplainListingAsync("saved games and one never-listed game");
    }

    private async Task ExplainListingAsync(string label)
    {
        recorder.Commands.Clear();
        (await host.Client.GetAsync(Listing)).EnsureSuccessStatusCode();
        var reads = recorder.Commands.Where(c => c.Sql.Contains("pro_hype_evidence_changes")).ToArray();
        output.WriteLine($"== {label}: {reads.Length} evidence-change commands");
        foreach (var read in reads)
        {
            await using var db = Context();
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + read.Sql, connection);
            explain.Parameters.AddRange(read.Parameters.Select(p => new NpgsqlParameter(p.Name, p.Value)).ToArray());
            await using var reader = await explain.ExecuteReaderAsync();
            while (await reader.ReadAsync()) output.WriteLine("    | " + reader.GetString(0));
        }
    }

    private async Task<long> SeedSavedSlateAsync(int games, int refreshes, int laterTeam)
    {
        for (var index = 0; index < games; index++) await SeedGameAsync(2 * index, ratings: true);
        await Change(db =>
        {
            db.TeamRatings.AddRange(Ratings(LaterGame(laterTeam), laterTeam));
            return Task.CompletedTask;
        });
        await Change(db =>
        {
            db.Database.SetCommandTimeout(TimeSpan.FromMinutes(30));
            return db.Database.ExecuteSqlRawAsync(
            "DO $$ BEGIN FOR i IN 1.." + refreshes.ToString(CultureInfo.InvariantCulture)
            + " LOOP UPDATE team_ratings SET fetched_at = fetched_at + interval '1 microsecond' WHERE source = 'kalshi';"
            + " IF i <= 50 THEN INSERT INTO team_ratings (sport, team_abbreviation, source, season, fetched_at, playoff_probability_is_final)"
            + " VALUES ('nhl', 'GONE', 'retired', 2027, now(), false); DELETE FROM team_ratings WHERE source = 'retired'; END IF;"
            + " END LOOP; END $$");
        });
        (await host.Client.GetAsync(Listing)).EnsureSuccessStatusCode();
        await using var db = Context();
        var snapshots = await db.ProHypeSnapshots.AsNoTracking().ToArrayAsync();
        Assert.Equal(games, snapshots.Count(s => s.SnapshotJson != null));
        return snapshots.Max(s => s.EvidenceRevision);
    }

    private async Task SeedGameAsync(int team, bool ratings)
    {
        var game = LaterGame(team);
        await Change(db =>
        {
            db.Games.Add(game);
            db.PregameOdds.Add(ProHypeTests.Odds(game));
            if (ratings) db.TeamRatings.AddRange(Ratings(game, team));
            return Task.CompletedTask;
        });
    }

    private static Game LaterGame(int team)
    {
        var game = ProHypeTests.Game();
        game.Id = "evidence-" + team.ToString(CultureInfo.InvariantCulture);
        game.HomeTeamAbbreviation = "H" + team.ToString(CultureInfo.InvariantCulture);
        game.AwayTeamAbbreviation = "A" + team.ToString(CultureInfo.InvariantCulture);
        game.ScheduledStart = ProHypeTests.Now.AddDays(6);
        game.ProEvidenceJson = JsonSerializer.Serialize(ProHypeContinuityInputs.Evidence(game));
        return game;
    }

    private static IEnumerable<TeamRating> Ratings(Game game, int team) => ProHypeTests.Ranks(game, team + 1, team + 2).Concat(
        new[] { game.HomeTeamAbbreviation, game.AwayTeamAbbreviation }.Select(abbreviation => new TeamRating
        {
            Sport = game.Sport, TeamAbbreviation = abbreviation, Season = game.SeasonYear!.Value, SourceSeason = game.SeasonYear,
            Source = "kalshi", PlayoffProbability = .5, FetchedAt = ProHypeTests.Now.AddMinutes(-1)
        }));

    private ExcitementDbContext Context() => host.Services.CreateScope().ServiceProvider.GetRequiredService<ExcitementDbContext>();

    private async Task Change(Func<ExcitementDbContext, Task> action)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await action(db);
        await db.SaveChangesAsync();
    }
}

public sealed class EvidenceReadProbeFactAttribute : FactAttribute
{
    internal const string DaysVariable = "NAILBITER_EVIDENCE_PROBE_DAYS";

    public EvidenceReadProbeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(DaysVariable) is null)
            Skip = "Measurement for PR #201; set NAILBITER_EVIDENCE_PROBE_DAYS=30 to run it";
    }
}
