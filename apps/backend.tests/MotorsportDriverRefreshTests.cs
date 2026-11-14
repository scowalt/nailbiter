using System.Collections.Concurrent;
using System.Net;
using System.Text;
using backend.Data;
using backend.Models;
using backend.Services.EspnF1;
using backend.Services.EspnIndyCar;
using backend.Services.F1;
using backend.Services.IndyCar;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;
using Xunit;

namespace backend.tests;

public sealed class MotorsportDriverRefreshTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer database = IsolatedPostgreSql.CreateContainer();
    private readonly AppFactory entryPoint = new();
    private readonly EspnHandler espn = new();
    private WebApplicationFactory<Program> app = null!;

    private static readonly DateTime PastStart = DateTime.UtcNow.Date.AddDays(-3).AddHours(17);
    private static readonly DateTime UpcomingStart = DateTime.UtcNow.Date.AddDays(2).AddHours(17);
    private static readonly int Year = UpcomingStart.Year;

    private sealed class EspnHandler : HttpMessageHandler
    {
        public ConcurrentQueue<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"events":[]}""", Encoding.UTF8, "application/json")
            });
        }
    }

    public async Task InitializeAsync()
    {
        await database.StartAsync();
        app = entryPoint.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
            services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
            services.AddDbContext<ExcitementDbContext>(options => options.UseNpgsql(database.GetConnectionString()));
            services.AddHttpClient<EspnF1Service>().ConfigurePrimaryHttpMessageHandler(() => espn);
            services.AddHttpClient<EspnIndyCarService>().ConfigurePrimaryHttpMessageHandler(() => espn);
        }));
        using var scope = app.Services.CreateScope();
        await TestSchema.CreateAsync(scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database);
    }

    public async Task DisposeAsync()
    {
        await app.DisposeAsync();
        await entryPoint.DisposeAsync();
        await database.DisposeAsync();
    }

    private async Task Seed(params object[] races)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        db.AddRange(races);
        await db.SaveChangesAsync();
    }

    private async Task<int> RefreshDrivers<TService>(Func<TService, Task<int>> refresh) where TService : notnull
    {
        using var scope = app.Services.CreateScope();
        return await refresh(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private static F1Race F1(string id, int round, DateTime start) => new()
    {
        Id = id, EspnEventId = id, MeetingName = id, CircuitName = id, CircuitShortName = id, Country = "",
        Year = Year, RoundNumber = round, ScheduledStart = start, Status = "scheduled"
    };

    private static IndyCarRace IndyCar(string id, int round, DateTime start) => new()
    {
        Id = id, EspnEventId = id, RaceName = id, Year = Year, RaceNumber = round, ScheduledStart = start, Status = "scheduled"
    };

    [Fact]
    public async Task F1_driver_refresh_without_a_final_race_uses_the_next_upcoming_race()
    {
        Assert.Equal(0, await RefreshDrivers<F1DataService>(service => service.RefreshDriversAsync(Year, CancellationToken.None)));
        Assert.Empty(espn.Requests);

        await Seed(F1("stale-f1", 1, PastStart), F1("upcoming-f1", 2, UpcomingStart));
        Assert.Equal(0, await RefreshDrivers<F1DataService>(service => service.RefreshDriversAsync(Year, CancellationToken.None)));
        Assert.Equal($"?dates={UpcomingStart:yyyyMMdd}", Assert.Single(espn.Requests).Query);
    }

    [Fact]
    public async Task IndyCar_driver_refresh_without_a_final_race_uses_the_next_upcoming_race()
    {
        Assert.Equal(0, await RefreshDrivers<IndyCarDataService>(service => service.RefreshDriversAsync(Year, CancellationToken.None)));
        Assert.Empty(espn.Requests);

        await Seed(IndyCar("stale-indycar", 1, PastStart), IndyCar("upcoming-indycar", 2, UpcomingStart));
        Assert.Equal(0, await RefreshDrivers<IndyCarDataService>(service => service.RefreshDriversAsync(Year, CancellationToken.None)));
        Assert.Equal($"?dates={UpcomingStart:yyyyMMdd}", Assert.Single(espn.Requests).Query);
    }
}
