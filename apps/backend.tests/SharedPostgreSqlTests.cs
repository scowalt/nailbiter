using backend.Data;
using backend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace backend.tests;

public sealed class SharedPostgreSqlTests(SharedPostgreSql server) : IClassFixture<SharedPostgreSql>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 18, 0, 0, TimeSpan.Zero);
    private readonly HostedGameFixture host = new(Now, server: server);

    public Task InitializeAsync() => host.InitializeAsync();
    public Task DisposeAsync() => host.DisposeAsync();

    [Theory]
    [InlineData("Alpha Home")]
    [InlineData("Beta Home")]
    public async Task Cases_that_seed_the_same_game_id_see_only_their_own_row(string homeTeam)
    {
        await host.SeedAsync(Game("same-id", homeTeam), []);

        var page = await host.Client.GetStringAsync("/game/same-id");

        Assert.Contains(homeTeam, page);
        Assert.DoesNotContain(homeTeam == "Alpha Home" ? "Beta Home" : "Alpha Home", page);
        Assert.Equal(homeTeam, Assert.Single(await GamesAsync(host)).HomeTeam);
    }

    [Fact]
    public async Task Concurrent_cases_keep_separate_databases_clocks_and_registrations()
    {
        var hosts = Enumerable.Range(0, 4)
            .Select(index => new HostedGameFixture(Now, services => services.AddSingleton(new Marker(index)), server))
            .ToArray();
        try
        {
            await Task.WhenAll(hosts.Select(h => h.InitializeAsync()));
            await Task.WhenAll(hosts.Select((h, index) => h.SeedAsync(Game("same-id", $"Home {index}"), [])));
            hosts[0].Clock.Advance(TimeSpan.FromDays(1));

            for (var index = 0; index < hosts.Length; index++)
            {
                Assert.Equal($"Home {index}", Assert.Single(await GamesAsync(hosts[index])).HomeTeam);
                Assert.Equal(index, hosts[index].Services.GetRequiredService<Marker>().Index);
                Assert.Equal(index == 0 ? Now.AddDays(1) : Now, hosts[index].Services.GetRequiredService<TimeProvider>().GetUtcNow());
            }
        }
        finally
        {
            await Task.WhenAll(hosts.Select(h => h.DisposeAsync()));
        }
    }

    [Fact]
    public async Task Disposing_a_case_drops_only_its_own_database()
    {
        var other = new HostedGameFixture(Now, server: server);
        await other.InitializeAsync();
        string connectionString;
        using (var scope = other.Services.CreateScope())
            connectionString = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.GetConnectionString()!;
        await other.SeedAsync(Game("other", "Other Home"), []);

        await other.DisposeAsync();

        await using var connection = new NpgsqlConnection(connectionString);
        var error = await Assert.ThrowsAsync<PostgresException>(() => connection.OpenAsync());
        Assert.Equal(PostgresErrorCodes.InvalidCatalogName, error.SqlState);
        await host.SeedAsync(Game("still-here", "Remaining Home"), []);
        Assert.Equal("Remaining Home", Assert.Single(await GamesAsync(host)).HomeTeam);
    }

    private static async Task<List<Game>> GamesAsync(HostedGameFixture fixture)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Games.AsNoTracking().ToListAsync();
    }

    private static Game Game(string id, string homeTeam) => new()
    {
        Id = id, Sport = "nhl", Status = "scheduled", SeasonYear = 2027, SeasonType = 2,
        HomeTeam = homeTeam, AwayTeam = "Away", HomeTeamAbbreviation = "HOM", AwayTeamAbbreviation = "AWY",
        ScheduledStart = Now.UtcDateTime.AddHours(3)
    };

    private sealed record Marker(int Index);
}
