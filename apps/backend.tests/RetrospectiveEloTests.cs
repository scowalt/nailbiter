using backend.Data;
using backend.Models;
using backend.Services.Backfill;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace backend.tests;

public sealed class RetrospectiveEloTests : IAsyncLifetime
{
    private readonly IsolatedPostgreSql database = new IsolatedPostgreSql();
    private readonly AppFactory entryPoint = new();
    private WebApplicationFactory<Program> app = null!;

    public async Task InitializeAsync()
    {
        await database.StartAsync();
        app = entryPoint.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
            services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
            services.AddDbContext<ExcitementDbContext>(options => options.UseNpgsql(database.GetConnectionString()));
        }));
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await app.DisposeAsync();
        await entryPoint.DisposeAsync();
        await database.DisposeAsync();
    }

    private static Game Nfl(string id, DateTime start, int? homeScore, int? awayScore) => new()
    {
        Id = id, Sport = "nfl", HomeTeam = "Home", AwayTeam = "Away", HomeTeamAbbreviation = "KC", AwayTeamAbbreviation = "BUF",
        ScheduledStart = start, Status = "final", SeasonYear = 2030, HomeScore = homeScore, AwayScore = awayScore
    };

    [Fact]
    public async Task Season_elo_rates_only_scored_final_games()
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        db.Games.AddRange(
            Nfl("scored", new DateTime(2030, 9, 8, 17, 0, 0, DateTimeKind.Utc), 24, 17),
            Nfl("unscored", new DateTime(2030, 9, 15, 17, 0, 0, DateTimeKind.Utc), null, null));
        await db.SaveChangesAsync();

        var created = await scope.ServiceProvider.GetRequiredService<RetrospectiveEloService>()
            .CalculateSeasonEloAsync(2030, CancellationToken.None);

        Assert.Equal(64, created);
        Assert.Equal(
            [new DateTime(2030, 9, 8), new DateTime(2030, 9, 9)],
            await db.TeamRatings.Select(rating => rating.FetchedAt).Distinct().OrderBy(date => date).ToListAsync());
    }
}
