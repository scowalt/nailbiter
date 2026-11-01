using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Docker.DotNet.Models;
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
        return builder.WithCreateParameterModifier(parameters =>
        {
            var bindings = parameters.HostConfig?.PortBindings ?? throw new InvalidOperationException("PostgreSQL port bindings missing.");
            bindings[$"{PostgreSqlBuilder.PostgreSqlPort}/tcp"] =
                [new PortBinding { HostPort = SelectDisposableHostPort().ToString(CultureInfo.InvariantCulture) }];
        });
    }

    private static void ConfigureTestcontainers()
    {
        var endpoint = TestcontainersSettings.OS.DockerEndpointAuthConfig.Endpoint;
        if (endpoint.Scheme == "unix") TestcontainersSettings.DockerSocketOverride = endpoint.AbsolutePath;
        TestcontainersSettings.ResourceReaperEnabled = true;
#pragma warning disable CS0618
        if (endpoint.Scheme == "unix") TestcontainersSettings.ResourceReaperPublicHostPort = _ => SelectDisposableHostPort();
#pragma warning restore CS0618
    }

    private static ushort SelectDisposableHostPort()
    {
        using var reservation = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp)
        {
            DualMode = true,
            ExclusiveAddressUse = true
        };
        reservation.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        return checked((ushort)((IPEndPoint)reservation.LocalEndPoint!).Port);
    }
}
