using Docker.DotNet.Models;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace backend.tests;

public sealed class RyukHostPortTests
{
    [Fact]
    public async Task Ryuk_publishes_its_host_port_above_the_OS_ephemeral_range()
    {
        await using var container = IsolatedPostgreSql.CreateContainer();
        await container.StartAsync();
        Assert.InRange(await PublishedRyukHostPort(ResourceReaper.DefaultSessionId), EphemeralUpperBound() + 1, ushort.MaxValue);
    }

    internal static int EphemeralUpperBound() =>
        int.Parse(File.ReadAllText("/proc/sys/net/ipv4/ip_local_port_range").Split('\t', ' ')[^1]);

    internal static async Task<ushort> PublishedRyukHostPort(Guid sessionId) =>
        Assert.Single(Assert.Single(await RyukContainers(sessionId)).Ports.Where(port => port.PrivatePort == 8080).Select(port => port.PublicPort.GetValueOrDefault()).Distinct());

    internal static async Task<IList<ContainerListResponse>> RyukContainers(Guid sessionId)
    {
        using var client = TestcontainersSettings.OS.DockerEndpointAuthConfig.GetDockerClientBuilder(Guid.Empty).Build();
        return await client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["name"] = new Dictionary<string, bool> { [$"testcontainers-ryuk-{sessionId:D}"] = true }
            }
        });
    }
}
