using System.Data.Common;
using System.Net;
using System.Text.Json;
using backend.Data;
using backend.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace backend.tests;

public sealed class GameOddsTests : IAsyncLifetime
{
    private readonly IsolatedPostgreSql database = new IsolatedPostgreSql();
    private readonly AppFactory entryPoint = new();
    private readonly CommandRecorder recorder = new();
    private WebApplicationFactory<Program> app = null!;
    private HttpClient client = null!;
    private static readonly DateTime Start = new(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        await database.StartAsync();
        app = entryPoint.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
            services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
            services.AddDbContext<ExcitementDbContext>(options =>
                options.UseNpgsql(database.GetConnectionString()).AddInterceptors(recorder));
        }));
        client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Games.AddRange(Game("target"), Game("other"), Game("empty"));
        foreach (var i in Enumerable.Range(0, 105).Reverse())
        {
            db.OddsSnapshots.Add(new OddsSnapshot
            {
                GameId = "target", Timestamp = Start.AddMinutes(i),
                HomeWinProbability = i / 200.0, AwayWinProbability = 1 - i / 200.0,
                HomeScore = i, AwayScore = 2, Period = 3, GameClock = "4:56"
            });
            db.OddsSnapshots.Add(new OddsSnapshot
            {
                GameId = "other", Timestamp = Start.AddDays(1).AddMinutes(i),
                HomeWinProbability = 0.99, AwayWinProbability = 0.01
            });
        }
        await db.SaveChangesAsync();
        recorder.Commands.Clear();
    }

    [Fact]
    public async Task Odds_lookup_filters_snapshots_by_game_in_the_database()
    {
        using var response = await client.GetAsync("/api/games/target/odds");
        response.EnsureSuccessStatusCode();

        var commands = recorder.Commands.Where(c => c.Sql.Contains("odds_snapshots")).ToArray();
        Assert.NotEmpty(commands);
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync();
        foreach (var captured in commands)
        {
            await using var command = new NpgsqlCommand("EXPLAIN (FORMAT JSON) " + captured.Sql, connection);
            foreach (var (name, value) in captured.Parameters)
                command.Parameters.AddWithValue(name, value);
            using var plan = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
            var scans = Descendants(plan.RootElement[0].GetProperty("Plan"))
                .Where(node => node.TryGetProperty("Relation Name", out var relation)
                    && relation.GetString() == "odds_snapshots").ToArray();
            Assert.NotEmpty(scans);
            foreach (var scan in scans)
            {
                var predicate = string.Join(" ", new[] { "Filter", "Index Cond", "Recheck Cond" }
                    .Where(name => scan.TryGetProperty(name, out _)).Select(name => scan.GetProperty(name).GetString()));
                Assert.True(predicate.Contains("game_id") && predicate.Contains("'target'"),
                    "Odds scans must be restricted to the requested game, not rank all games. Plan: " + scan);
            }
        }
    }

    [Fact]
    public async Task Odds_history_contains_only_the_latest_100_for_the_game_in_chronological_order()
    {
        using var response = await client.GetAsync("/api/games/target/odds");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var game = json.RootElement;
        Assert.Equal("target", game.GetProperty("id").GetString());
        Assert.Equal("Home", game.GetProperty("homeTeam").GetString());
        Assert.Equal("Away", game.GetProperty("awayTeam").GetString());
        Assert.Equal("in_progress", game.GetProperty("status").GetString());
        var history = game.GetProperty("oddsHistory").EnumerateArray().ToArray();
        Assert.Equal(100, history.Length);
        Assert.Equal(Start.AddMinutes(5), history[0].GetProperty("timestamp").GetDateTime());
        Assert.Equal(Start.AddMinutes(104), history[^1].GetProperty("timestamp").GetDateTime());
        Assert.Equal(0.025, history[0].GetProperty("homeWinProbability").GetDouble());
        Assert.Equal(0.975, history[0].GetProperty("awayWinProbability").GetDouble());
        Assert.Equal(5, history[0].GetProperty("homeScore").GetInt32());
        Assert.Equal(2, history[0].GetProperty("awayScore").GetInt32());
        Assert.Equal(3, history[0].GetProperty("period").GetInt32());
        Assert.Equal("4:56", history[0].GetProperty("gameClock").GetString());
        Assert.All(history.Zip(history.Skip(1)), pair =>
            Assert.True(pair.First.GetProperty("timestamp").GetDateTime()
                < pair.Second.GetProperty("timestamp").GetDateTime()));
        Assert.All(recorder.Commands, command => Assert.True(command.CanBeCanceled));
    }

    [Fact]
    public async Task Existing_game_without_snapshots_returns_an_empty_history()
    {
        using var response = await client.GetAsync("/api/games/empty/odds");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("empty", json.RootElement.GetProperty("id").GetString());
        Assert.Empty(json.RootElement.GetProperty("oddsHistory").EnumerateArray());
    }

    [Fact]
    public async Task Missing_game_returns_not_found_without_reading_snapshots()
    {
        using var response = await client.GetAsync("/api/games/missing/odds");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(recorder.Commands, command => command.Sql.Contains("odds_snapshots"));
    }

    private static IEnumerable<JsonElement> Descendants(JsonElement node)
    {
        yield return node;
        if (node.TryGetProperty("Plans", out var children))
            foreach (var child in children.EnumerateArray())
                foreach (var descendant in Descendants(child))
                    yield return descendant;
    }

    private static Game Game(string id) => new()
    {
        Id = id, Sport = "mlb", HomeTeam = "Home", AwayTeam = "Away",
        HomeTeamAbbreviation = "H", AwayTeamAbbreviation = "A",
        ScheduledStart = Start, Status = "in_progress"
    };

    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (app is not null) await app.DisposeAsync();
        await entryPoint.DisposeAsync();
        await database.DisposeAsync();
    }

    private sealed record CapturedCommand(string Sql, (string Name, object Value)[] Parameters, bool CanBeCanceled);

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<CapturedCommand> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(new(command.CommandText, command.Parameters.Cast<DbParameter>()
                .Select(p => (p.ParameterName, p.Value!)).ToArray(), cancellationToken.CanBeCanceled));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
