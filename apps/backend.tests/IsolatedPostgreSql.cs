using System.Globalization;
using DotNet.Testcontainers.Configurations;
using Testcontainers.PostgreSql;

namespace backend.tests;

internal static class IsolatedPostgreSql
{
    public static PostgreSqlContainer CreateContainer() => CreateBuilder().Build();

    public static PostgreSqlBuilder CreateBuilder()
    {
        ConfigureTestcontainers();
        var id = Guid.NewGuid().ToString("N");
        var builder = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase($"nailbiter_test_{id}")
            .WithPassword($"nailbiter-test-{id}");
        if (TestcontainersSettings.OS.DockerEndpointAuthConfig.Endpoint.Scheme != "unix") return builder;
        return builder.WithPortBinding(DisposableHostPorts.Range, PostgreSqlBuilder.PostgreSqlPort.ToString(CultureInfo.InvariantCulture));
    }

    private static void ConfigureTestcontainers()
    {
        var endpoint = TestcontainersSettings.OS.DockerEndpointAuthConfig.Endpoint;
        if (endpoint.Scheme == "unix") TestcontainersSettings.DockerSocketOverride = endpoint.AbsolutePath;
        TestcontainersSettings.ResourceReaperEnabled = true;
#pragma warning disable CS0618
        if (endpoint.Scheme == "unix") TestcontainersSettings.ResourceReaperPublicHostPort = _ => DisposableHostPorts.SelectFree();
#pragma warning restore CS0618
    }
}
