using System.Diagnostics;
using System.Text.Json;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace backend.tests;

public sealed class DatabaseSafeguardTests
{
    [Fact]
    public async Task Application_connections_enforce_a_server_timeout_without_changing_migration_connections()
    {
        var start = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "compose", "-f", Path.Combine(AppContext.BaseDirectory, "docker-compose.yml"), "config", "--format", "json" })
            start.ArgumentList.Add(argument);
        start.Environment["POSTGRES_PASSWORD"] = "integration-test-placeholder";
        start.Environment["LOKI_URL"] = "https://example.invalid";
        start.Environment["LOKI_USERNAME"] = "test";
        start.Environment["GRAFANA_CLOUD_API_KEY"] = "test";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, "Unable to render Docker Compose: " + await errors);
        using var compose = JsonDocument.Parse(await output);
        var deployed = new NpgsqlConnectionStringBuilder(compose.RootElement.GetProperty("services")
            .GetProperty("backend").GetProperty("environment")
            .GetProperty("ConnectionStrings__DefaultConnection").GetString());

        var serverArguments = compose.RootElement.GetProperty("services").GetProperty("timescaledb")
            .GetProperty("command").EnumerateArray().Select(value => value.GetString()!).ToArray();
        await using var database = IsolatedPostgreSql.CreateBuilder().WithCommand(serverArguments).Build();
        await database.StartAsync();
        var application = new NpgsqlConnectionStringBuilder(database.GetConnectionString())
        {
            Options = deployed.Options
        };
        await using var connection = new NpgsqlConnection(application.ConnectionString);
        await connection.OpenAsync();
        await using var setting = new NpgsqlCommand("SHOW statement_timeout", connection);
        Assert.Equal("30s", await setting.ExecuteScalarAsync());

        await using var slowQuery = new NpgsqlCommand("SELECT pg_sleep(35)", connection) { CommandTimeout = 0 };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var timedOut = Assert.ThrowsAsync<PostgresException>(() => slowQuery.ExecuteScalarAsync(deadline.Token));
        await StallTheDatabaseAcrossItsStatementTimeoutAsync(database);
        var failure = await timedOut;
        Assert.Equal(PostgresErrorCodes.QueryCanceled, failure.SqlState);
        Assert.Contains("statement timeout", failure.MessageText);
        await using var recoveredQuery = new NpgsqlCommand("SELECT 1", connection);
        Assert.Equal(1, await recoveredQuery.ExecuteScalarAsync());

        await using var migration = new NpgsqlConnection(database.GetConnectionString());
        await migration.OpenAsync();
        await using var migrationSetting = new NpgsqlCommand("SHOW statement_timeout", migration);
        Assert.Equal("0", await migrationSetting.ExecuteScalarAsync());
    }

    private static async Task StallTheDatabaseAcrossItsStatementTimeoutAsync(PostgreSqlContainer database)
    {
        await Task.Delay(TimeSpan.FromSeconds(25));
        await database.PauseAsync();
        await Task.Delay(TimeSpan.FromSeconds(21));
        await database.UnpauseAsync();
    }
}
