using System.Globalization;
using System.Net;
using System.Net.Sockets;
using backend.Data;
using Docker.DotNet.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace backend.tests;

public sealed class IsolatedPostgreSqlTests
{
    [Fact]
    public async Task PostgreSQL_starts_when_another_listener_takes_the_requested_host_port_before_start()
    {
        var competitors = new List<Socket>();
        try
        {
            await using var container = IsolatedPostgreSql.CreateBuilder()
                .WithCreateParameterModifier(parameters =>
                {
                    foreach (var binding in parameters.HostConfig!.PortBindings.Values.SelectMany(bindings => bindings))
                        if (ushort.TryParse(binding.HostPort, out var port) && port != 0)
                            competitors.Add(Listen(port));
                })
                .Build();
            await container.StartAsync();
            await using var connection = new NpgsqlConnection(container.GetConnectionString());
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("select 1", connection);
            Assert.Equal(1, await command.ExecuteScalarAsync());
        }
        finally
        {
            foreach (var competitor in competitors) competitor.Dispose();
        }
    }

    private static Socket Listen(ushort port)
    {
        var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
        socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        socket.Listen();
        return socket;
    }

    [Fact]
    public async Task Real_host_initializes_second_database_after_owned_OS_port_reuse_with_normal_pooling()
    {
        using var reservation = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp)
        {
            DualMode = true,
            ExclusiveAddressUse = true
        };
        reservation.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        var port = ((IPEndPoint)reservation.LocalEndPoint!).Port.ToString(CultureInfo.InvariantCulture);
        reservation.Close();
        for (var index = 0; index < 2; index++)
        {
            await using var container = IsolatedPostgreSql.CreateBuilder()
                .WithCreateParameterModifier(parameters =>
                    parameters.HostConfig!.PortBindings["5432/tcp"] = [new PortBinding { HostPort = port }])
                .Build();
            await container.StartAsync();
            var connectionString = container.GetConnectionString();
            Assert.True(new NpgsqlConnectionStringBuilder(connectionString).Pooling);
            await using var factory = new AppFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
                services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
                services.AddDbContext<ExcitementDbContext>(options => options.UseNpgsql(connectionString));
            }));
            using var client = factory.CreateClient();
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            await db.Database.EnsureCreatedAsync();
            Assert.Contains("\"status\":\"healthy\"", await client.GetStringAsync("/health"));
        }
    }
}
