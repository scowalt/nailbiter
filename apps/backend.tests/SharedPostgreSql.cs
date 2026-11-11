using backend.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace backend.tests;

public sealed class SharedPostgreSql : IAsyncLifetime
{
    private const string Template = "nailbiter_template";
    private readonly PostgreSqlContainer container = IsolatedPostgreSql.CreateContainer();
    private readonly string timingOwner = "server-" + Guid.NewGuid().ToString("N");

    public async Task InitializeAsync()
    {
        await FixtureTimings.MeasureAsync(timingOwner, "container", () => container.StartAsync());
        await FixtureTimings.MeasureAsync(timingOwner, "schema", CreateTemplateAsync);
    }

    internal async Task<TestDatabase> CreateDatabaseAsync()
    {
        var name = "nailbiter_case_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE DATABASE {name} TEMPLATE {Template}");
        var connectionString = ConnectionString(name, pooling: true);
        return new TestDatabase(connectionString, async () =>
        {
            NpgsqlConnection.ClearPool(new NpgsqlConnection(connectionString));
            await ExecuteAsync($"DROP DATABASE {name} WITH (FORCE)");
        });
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    private async Task CreateTemplateAsync()
    {
        await ExecuteAsync($"CREATE DATABASE {Template}");
        var options = new DbContextOptionsBuilder<ExcitementDbContext>()
            .UseNpgsql(ConnectionString(Template, pooling: false)).Options;
        await using var db = new ExcitementDbContext(options);
        await db.Database.EnsureCreatedAsync();
    }

    private string ConnectionString(string database, bool pooling) =>
        new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = database, Pooling = pooling }.ConnectionString;

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
