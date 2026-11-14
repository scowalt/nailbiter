using backend.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace backend.tests;

public sealed class SharedPostgreSql : IAsyncLifetime
{
    private const string Template = "nailbiter_template";
    private readonly PostgreSqlContainer container;
    private readonly Func<string, Task>? prepareTemplate;
    private readonly string timingOwner = "server-" + Guid.NewGuid().ToString("N");

    public SharedPostgreSql() : this(IsolatedPostgreSql.CreateContainer()) { }

    internal SharedPostgreSql(PostgreSqlContainer container, Func<string, Task>? prepareTemplate = null)
    {
        this.container = container;
        this.prepareTemplate = prepareTemplate;
    }

    public async Task InitializeAsync()
    {
        await FixtureTimings.MeasureAsync(timingOwner, "container", StartContainerAsync);
        await FixtureTimings.MeasureAsync(timingOwner, "schema", CreateTemplateAsync);
    }

    internal Task StartContainerAsync() => container.StartAsync();

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

    internal async Task CreateTemplateAsync()
    {
        await ExecuteAsync($"CREATE DATABASE {Template}");
        var options = new DbContextOptionsBuilder<ExcitementDbContext>()
            .UseNpgsql(ConnectionString(Template, pooling: false)).Options;
        await using (var db = new ExcitementDbContext(options))
            await db.Database.EnsureCreatedAsync();
        if (prepareTemplate is not null) await prepareTemplate(ConnectionString(Template, pooling: false));
        await using (var db = new ExcitementDbContext(options))
            await TestSchema.CreateAsync(db.Database);
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
