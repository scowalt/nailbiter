using Docker.DotNet;
using DotNet.Testcontainers.Configurations;
using Testcontainers.PostgreSql;

namespace backend.tests;

public sealed class IsolatedPostgreSql(Func<PostgreSqlBuilder, PostgreSqlBuilder>? configure = null) : IAsyncDisposable
{
    private const int StartAttempts = 3;
    private PostgreSqlContainer? container;

    public async Task StartAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            container = (configure ?? (builder => builder))(CreateBuilder()).Build();
            try
            {
                await container.StartAsync();
                return;
            }
            // Rootless Docker assigns host ports without seeing host listeners, so its port can already be taken.
            catch (DockerApiException error) when (attempt < StartAttempts && error.Message.Contains("address already in use", StringComparison.Ordinal))
            {
                await container.DisposeAsync();
            }
        }
    }

    public string GetConnectionString() =>
        (container ?? throw new InvalidOperationException("Start the database before reading its connection string.")).GetConnectionString();

    public ValueTask DisposeAsync() => container?.DisposeAsync() ?? ValueTask.CompletedTask;

    private static PostgreSqlBuilder CreateBuilder()
    {
        ConfigureTestcontainers();
        var id = Guid.NewGuid().ToString("N");
        return new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase($"nailbiter_test_{id}")
            .WithPassword($"nailbiter-test-{id}");
    }

    private static void ConfigureTestcontainers()
    {
        var endpoint = TestcontainersSettings.OS.DockerEndpointAuthConfig.Endpoint;
        if (endpoint.Scheme == "unix") TestcontainersSettings.DockerSocketOverride = endpoint.AbsolutePath;
        TestcontainersSettings.ResourceReaperEnabled = true;
    }
}
