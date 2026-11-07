using System.Text.Json;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace backend.tests;

public sealed class PlayoffSeriesLockOrderTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer database = IsolatedPostgreSql.CreateContainer();
    private readonly GameUpdateGate gameUpdates = new();
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
            .UseNpgsql(database.GetConnectionString()).AddInterceptors(gameUpdates).Options);
        await collector.Games.SingleAsync(g => g.Id == game.Id);
        gameUpdates.Arm();
        var collecting = PlayoffSeriesCollector.SaveAsync(collector,
            [new PlayoffSeriesObservation { GameId = game.Id, Source = "espn", ObservedAt = ProHypeTests.Now, Snapshot = null }],
            new HashSet<string>(), CancellationToken.None);
        await gameUpdates.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await using var refresh = new ExcitementDbContext(options);
        var stored = await refresh.Games.SingleAsync(g => g.Id == game.Id);
        stored.ProEvidenceJson = JsonSerializer.Serialize(ProHypeTests.Evidence(stored));
        var writing = refresh.SaveChangesAsync();
        await GameUpdateGate.WaitForLockWaitAsync(database.GetConnectionString());
        gameUpdates.Release();

        await Task.WhenAll(collecting, writing);
        await using var db = new ExcitementDbContext(options);
        var saved = await db.Games.AsNoTracking().SingleAsync(g => g.Id == game.Id);
        Assert.Equal(game.Id, PlayoffSeriesObservation.Parse(saved.SeriesObservationJson)?.GameId);
        Assert.Equal(game.Id, ProGameEvidence.Parse(saved.ProEvidenceJson)?.GameId);
    }
}
