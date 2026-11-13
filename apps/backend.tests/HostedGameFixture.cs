using System.Diagnostics;
using System.Net;
using backend.Data;
using backend.Models;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace backend.tests;

internal sealed class HostedGameFixture : IAsyncLifetime
{
    private readonly AppFactory entry = new();
    private readonly Action<IServiceCollection>? configureServices;
    private readonly SharedPostgreSql? server;
    private readonly bool listenOnLoopback;
    private int rejectedOutboundRequests;
    private TestDatabase? database;
    private WebApplicationFactory<Program>? app;
    private readonly string timingOwner = "case-" + Guid.NewGuid().ToString("N");
    private long initializedAt;
    private TimeSpan seeding;

    public FakeTimeProvider Clock { get; }
    public HttpClient Client { get; private set; } = null!;
    public IServiceProvider Services => app!.Services;
    public int RejectedOutboundRequests => Volatile.Read(ref rejectedOutboundRequests);

    public HostedGameFixture(DateTimeOffset? now = null, Action<IServiceCollection>? configureServices = null, SharedPostgreSql? server = null,
        bool listenOnLoopback = false)
    {
        Clock = new FakeTimeProvider(now ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        this.configureServices = configureServices;
        this.server = server;
        this.listenOnLoopback = listenOnLoopback;
    }

    public async Task InitializeAsync()
    {
        database = server is null
            ? await FixtureTimings.MeasureAsync(timingOwner, "container", TestDatabase.StartContainerAsync)
            : await FixtureTimings.MeasureAsync(timingOwner, "database", server.CreateDatabaseAsync);
        var connectionString = database.ConnectionString;
        var hostStarted = Stopwatch.GetTimestamp();
        app = entry.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
            services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
            services.AddDbContext<ExcitementDbContext>(options => options.UseNpgsql(connectionString));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IHttpMessageHandlerBuilderFilter>(
                new RejectOutboundRequests(() => Interlocked.Increment(ref rejectedOutboundRequests)));
            configureServices?.Invoke(services);
        }));
        if (listenOnLoopback)
        {
            app.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
            app.StartServer();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            Client = new HttpClient { BaseAddress = new Uri(address) };
        }
        else
            Client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        FixtureTimings.Record(timingOwner, "host", Stopwatch.GetElapsedTime(hostStarted));
        using var scope = Services.CreateScope();
        await FixtureTimings.MeasureAsync(timingOwner, "schema",
            () => scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.EnsureCreatedAsync());
        initializedAt = Stopwatch.GetTimestamp();
    }

    public async Task<SyntheticExcitementHistory> SeedSyntheticHistoryAsync(string gameId)
    {
        var capture = SyntheticExcitementHistory.Load(gameId);
        await SeedAsync(capture.CreateGame(), capture.CreateSnapshots().Reverse());
        return capture;
    }

    public async Task SeedAsync(Game game, IEnumerable<OddsSnapshot> snapshots)
    {
        var started = Stopwatch.GetTimestamp();
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        db.Games.Add(game);
        db.OddsSnapshots.AddRange(snapshots);
        await db.SaveChangesAsync();
        seeding += Stopwatch.GetElapsedTime(started);
    }

    public async Task DisposeAsync()
    {
        if (initializedAt != 0)
        {
            FixtureTimings.Record(timingOwner, "seed", seeding);
            FixtureTimings.Record(timingOwner, "body", Stopwatch.GetElapsedTime(initializedAt) - seeding);
        }
        var disposeStarted = Stopwatch.GetTimestamp();
        Client?.Dispose();
        if (app is not null) await app.DisposeAsync();
        await entry.DisposeAsync();
        if (database is not null) await database.DisposeAsync();
        FixtureTimings.Record(timingOwner, "dispose", Stopwatch.GetElapsedTime(disposeStarted));
    }

    private sealed class RejectOutboundRequests(Action rejected) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            builder.PrimaryHandler = new RejectHandler(rejected);
        };

        private sealed class RejectHandler(Action rejected) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                rejected();
                throw new InvalidOperationException("Live provider requests are forbidden in hosted game tests: " + request.RequestUri);
            }
        }
    }
}
