using System.Net;
using backend.Data;
using backend.Models;
using backend.Services.Espn;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;

namespace BrowserFixture;

internal sealed record FixtureGame(string Id, string AwayTeam, string AwayAbbreviation, int AwayScore,
    string HomeTeam, string HomeAbbreviation, int HomeScore, string Status = "final")
{
    public string DetailPath => $"/game/{Id}";
}

internal sealed class FixtureHost : WebApplicationFactory<global::Program>
{
    public static readonly FixtureGame Intended = new("pilot-nba-completed", "Pilot Harbor Otters", "PHO", 107,
        "Pilot Summit Owls", "PSO", 113);
    public static readonly FixtureGame Distraction = new("pilot-nba-distraction", "Pilot Canyon Foxes", "PCF", 98,
        "Pilot Meadow Cranes", "PMC", 101);

    private readonly string connectionString;
    private readonly string contentRoot;
    private readonly string webRoot;
    public DateTime ListingDate { get; private set; }
    public HighlightsFixture? Highlights { get; }
    public GroupedHomeFixture? GroupedHome { get; }
    public SeriesFixture? Series { get; }

    public FixtureHost(string connectionString, string contentRoot, string webRoot, int port, bool highlights = false, bool groupedHome = false, bool series = false)
    {
        this.connectionString = connectionString;
        this.contentRoot = contentRoot;
        this.webRoot = webRoot;
        Highlights = highlights ? new HighlightsFixture() : null;
        GroupedHome = groupedHome ? new GroupedHomeFixture() : null;
        Series = series ? new SeriesFixture() : null;
        UseKestrel(options => options.Listen(IPAddress.Loopback, port));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development").UseContentRoot(contentRoot).UseWebRoot(webRoot);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.Sources.Clear();
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connectionString,
                ["EnableScraper"] = "false",
                ["Sentry:Dsn"] = "",
                ["OpenTelemetry:Enabled"] = "false",
                ["Logging:LogLevel:Default"] = "Warning"
            });
        });
        builder.ConfigureServices(services =>
        {
            foreach (var worker in services.Where(s => s.ServiceType == typeof(IHostedService)
                && (s.ImplementationType?.Assembly == typeof(EspnOddsService).Assembly
                    || s.ImplementationType?.Namespace?.StartsWith("backend.Services", StringComparison.Ordinal) == true)).ToArray())
                services.Remove(worker);
            services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
            services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
            services.AddDbContext<ExcitementDbContext>(options => options.UseNpgsql(connectionString));
            services.ConfigureAll<HttpClientFactoryOptions>(options =>
                options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = new NoOutboundHttp()));
            Highlights?.Configure(services);
            GroupedHome?.Configure(services);
            Series?.Configure(services);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = builder.Build();
        try
        {
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            db.Database.EnsureCreated();
            var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            ListingDate = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, eastern).Date;
            var localNoon = DateTime.SpecifyKind(ListingDate.AddHours(12), DateTimeKind.Unspecified);
            var scheduledStart = TimeZoneInfo.ConvertTimeToUtc(localNoon, eastern);
            var season = ListingDate.Month >= 10 ? ListingDate.Year + 1 : ListingDate.Year;
            if (GroupedHome is null) db.Games.AddRange(new[] { Intended, Distraction }.Select(game => new Game
            {
                Id = game.Id,
                Sport = "nba",
                AwayTeam = game.AwayTeam,
                HomeTeam = game.HomeTeam,
                AwayTeamAbbreviation = game.AwayAbbreviation,
                HomeTeamAbbreviation = game.HomeAbbreviation,
                AwayScore = game.AwayScore,
                HomeScore = game.HomeScore,
                Status = game.Status,
                ScheduledStart = scheduledStart,
                SeasonYear = season,
                SeasonType = 2,
                Period = 4,
                GameClock = "0:00"
            }));
            db.SaveChanges();
            GroupedHome?.Seed(db);
            if (GroupedHome is not null) ListingDate = TimeZoneInfo.ConvertTime(GroupedHomeFixture.Now, eastern).Date;
            Highlights?.Seed(db);
            Highlights?.InitializeAsync(host.Services).GetAwaiter().GetResult();
            Series?.InitializeAsync(host.Services).GetAwaiter().GetResult();
            host.Start();
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private sealed class NoOutboundHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("Outbound backend HTTP is disabled in the browser fixture.");
    }
}
