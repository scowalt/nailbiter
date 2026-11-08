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
            await using var container = new IsolatedPostgreSql(builder => builder.WithCreateParameterModifier(parameters =>
                parameters.HostConfig!.PortBindings["5432/tcp"] = [new PortBinding { HostPort = port }]));
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
