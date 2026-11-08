using System.Text.Json;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace backend.tests;

public sealed class PlayoffSeriesLockOrderTests : IAsyncLifetime
{
    private readonly IsolatedPostgreSql database = new IsolatedPostgreSql();
    private readonly GameRowLockGate gameRowLocks = new();
    private readonly Game game = ProHypeTests.Game("mlb");
    private DbContextOptions<ExcitementDbContext> options = null!;

    public async Task InitializeAsync()
    {
        await database.StartAsync();
        options = new DbContextOptionsBuilder<ExcitementDbContext>().UseNpgsql(database.GetConnectionString()).Options;
        await using var db = new ExcitementDbContext(options);
        await db.Database.EnsureCreatedAsync();
        foreach (var migration in new[] { "034_PlayoffSeriesObservation.sql", "035_ProHypeLifecycle.sql" })
            await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", migration)));
        db.Games.Add(game);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => database.DisposeAsync().AsTask();

    [Fact]
    public async Task Series_collection_and_a_concurrent_pro_evidence_write_to_the_same_game_both_commit()
    {
        await using var collector = new ExcitementDbContext(new DbContextOptionsBuilder<ExcitementDbContext>()
            .UseNpgsql(database.GetConnectionString()).AddInterceptors(gameRowLocks).Options);
        await collector.Games.SingleAsync(g => g.Id == game.Id);
        gameRowLocks.Arm();
        var collecting = Collect(collector);
        await gameRowLocks.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await using var refresh = new ExcitementDbContext(options);
        var stored = await refresh.Games.SingleAsync(g => g.Id == game.Id);
        stored.ProEvidenceJson = JsonSerializer.Serialize(ProHypeTests.Evidence(stored));
        var writing = refresh.SaveChangesAsync();
        await GameRowLockGate.WaitForLockWaitAsync(database.GetConnectionString());
        gameRowLocks.Release();

        await Task.WhenAll(collecting, writing);
        var saved = await Saved();
        Assert.Equal(game.Id, PlayoffSeriesObservation.Parse(saved.SeriesObservationJson)?.GameId);
        Assert.Equal(game.Id, ProGameEvidence.Parse(saved.ProEvidenceJson)?.GameId);
    }

    [Fact]
    public async Task Series_collection_and_a_migration_replay_that_alters_then_writes_games_both_commit()
    {
        var replay = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "022_WorldCupOvertime.sql"));
        var write = replay.IndexOf("UPDATE games", StringComparison.Ordinal);
        await using var migration = new NpgsqlConnection(database.GetConnectionString());
        await migration.OpenAsync();
        await using var collector = new ExcitementDbContext(options);
        await collector.Games.SingleAsync(g => g.Id == game.Id);
        await new NpgsqlCommand(replay[..write], migration).ExecuteNonQueryAsync();

        var collecting = Collect(collector);
        await GameRowLockGate.WaitForLockWaitAsync(database.GetConnectionString());
        await new NpgsqlCommand(replay[write..], migration).ExecuteNonQueryAsync();

        await collecting.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(game.Id, PlayoffSeriesObservation.Parse((await Saved()).SeriesObservationJson)?.GameId);
    }

    private Task<HashSet<string>> Collect(ExcitementDbContext collector) => PlayoffSeriesCollector.SaveAsync(collector,
        [new PlayoffSeriesObservation { GameId = game.Id, Source = "espn", ObservedAt = ProHypeTests.Now, Snapshot = null }],
        new HashSet<string>(), CancellationToken.None);

    private async Task<Game> Saved()
    {
        await using var db = new ExcitementDbContext(options);
        return await db.Games.AsNoTracking().SingleAsync(g => g.Id == game.Id);
    }
}
