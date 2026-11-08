using backend.Data;
using backend.Models;
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
    private readonly IsolatedPostgreSql database = new IsolatedPostgreSql();
    private readonly AppFactory entry = new();
    private readonly Action<IServiceCollection>? configureServices;
    private WebApplicationFactory<Program>? app;

    public FakeTimeProvider Clock { get; }
    public HttpClient Client { get; private set; } = null!;
    public IServiceProvider Services => app!.Services;

    public HostedGameFixture(DateTimeOffset? now = null, Action<IServiceCollection>? configureServices = null)
    {
        Clock = new FakeTimeProvider(now ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        this.configureServices = configureServices;
    }

    public async Task InitializeAsync()
    {
        await database.StartAsync();
        app = entry.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
            services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
            services.AddDbContext<ExcitementDbContext>(options => options.UseNpgsql(database.GetConnectionString()));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IHttpMessageHandlerBuilderFilter, RejectOutboundRequests>();
            configureServices?.Invoke(services);
        }));
        Client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task<SyntheticExcitementHistory> SeedSyntheticHistoryAsync(string gameId)
    {
        var capture = SyntheticExcitementHistory.Load(gameId);
        await SeedAsync(capture.CreateGame(), capture.CreateSnapshots().Reverse());
        return capture;
    }

    public async Task SeedAsync(Game game, IEnumerable<OddsSnapshot> snapshots)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        db.Games.Add(game);
        db.OddsSnapshots.AddRange(snapshots);
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (app is not null) await app.DisposeAsync();
        await entry.DisposeAsync();
        await database.DisposeAsync();
    }

    private sealed class RejectOutboundRequests : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            builder.PrimaryHandler = new RejectHandler();
        };

        private sealed class RejectHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Live provider requests are forbidden in hosted game tests: " + request.RequestUri);
        }
    }
}
