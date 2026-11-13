using System.Net;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace backend.tests;

public sealed class MoneyPuckQualificationClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 11, 18, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class MoneyPuckQualificationHandler : HttpMessageHandler
{
    public const string MarkerUrl = "https://moneypuck.com/moneypuck/simulations/update_date.txt";
    public const string PageUrl = "https://moneypuck.com/predictions.htm";
    public const string CsvUrl = "https://moneypuck.com/moneypuck/simulations/simulations_recent.csv";
    public string Marker { get; set; } = Fixture("publication.txt").Trim();
    public string Page { get; set; } = Fixture("predictions.html");
    public string Csv { get; set; } = Fixture("simulations.csv");
    public string RankingMarker { get; set; } = "Sunday October 11, 2026 at 1:00 PM Eastern Time";
    public string RankingCsv { get; set; } = "teamCode,avg_overall_prediction\n" + string.Join('\n', new[] { "OTT", "NSH" }.Concat(MoneyPuckQualificationHarness.Teams.Where(team => team != "OTT" && team != "NSH")).Select(team => $"{team switch { "LA" => "LAK", "NJ" => "NJD", "SJ" => "SJS", "TB" => "TBL", "UTAH" => "UTA", _ => team }},0.5"));
    public List<string> Requests { get; } = [];
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? Override { get; set; }
    public static string Fixture(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MoneyPuckQualification", "Synthetic", file));
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests) Requests.Add(request.RequestUri!.AbsoluteUri);
        if (Override is not null && await Override(request, cancellationToken) is { } response) return response;
        var url = request.RequestUri!.AbsoluteUri;
        var text = url switch
        {
            MarkerUrl => Marker,
            PageUrl => Page,
            CsvUrl => Csv,
            MoneyPuckRankings.UpdatedUrl => RankingMarker,
            MoneyPuckRankings.RankingsUrl => RankingCsv,
            "https://api-web.nhle.com/v1/gamecenter/2099020002/landing" => Fixture("game-2099020002.json"),
            "https://api-web.nhle.com/v1/gamecenter/2099020003/landing" => Fixture("game-2099020003.json"),
            _ => throw new InvalidOperationException("Unexpected offline request: " + url)
        };
        return new(HttpStatusCode.OK) { Content = new StringContent(text) };
    }
}

public sealed class MoneyPuckQualificationHarness : IAsyncDisposable
{
    public const string Source = "moneypuck_qualification";
    public static readonly string[] Teams = ["ANA", "BOS", "BUF", "CGY", "CAR", "CHI", "COL", "CBJ", "DAL", "DET", "EDM", "FLA", "LA", "MIN", "MTL", "NSH", "NJ", "NYI", "NYR", "OTT", "PHI", "PIT", "SJ", "SEA", "STL", "TB", "TOR", "UTAH", "VAN", "VGK", "WSH", "WPG"];
    private readonly PostgreSqlContainer database = IsolatedPostgreSql.CreateContainer();
    private readonly AppFactory entryPoint = new();
    public MoneyPuckQualificationClock Clock { get; } = new();
    public MoneyPuckQualificationHandler Handler { get; } = new();
    public WebApplicationFactory<Program> App { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public PlayoffRatingsOptions Options { get; } = new() { MoneyPuckSelectionEnabled = true, MoneyPuckRefreshEnabled = true, MoneyPuckApprovedSeasons = [2027] };
    public async Task StartAsync(Action<DbContextOptionsBuilder>? configureDb = null, Action<IServiceCollection>? configureServices = null, bool enabled = true, bool configuredTransport = false)
    {
        await database.StartAsync();
        CreateApplication(configureDb, configureServices, enabled, configuredTransport);
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await TestSchema.CreateAsync(db.Database);
        await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX qualification_test_identity ON team_ratings (sport, team_abbreviation, season, source)");
    }
    public async Task RestartApplicationAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
        CreateApplication(null, null, true, false);
    }
    public WebApplicationFactory<Program> CreatePeerApplication() => CreateFactory(null, null, true, false);
    private void CreateApplication(Action<DbContextOptionsBuilder>? configureDb, Action<IServiceCollection>? configureServices, bool enabled, bool configuredTransport)
    {
        App = CreateFactory(configureDb, configureServices, enabled, configuredTransport);
        Client = App.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
    }
    private WebApplicationFactory<Program> CreateFactory(Action<DbContextOptionsBuilder>? configureDb, Action<IServiceCollection>? configureServices, bool enabled, bool configuredTransport)
    {
        return entryPoint.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlayoffRatings:MoneyPuckSelectionEnabled"] = enabled.ToString(),
                ["PlayoffRatings:MoneyPuckRefreshEnabled"] = enabled.ToString(),
                ["PlayoffRatings:MoneyPuckApprovedSeasons:0"] = "2027"
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
                services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
                services.AddDbContext<ExcitementDbContext>(options => { options.UseNpgsql(database.GetConnectionString()); configureDb?.Invoke(options); });
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
                if (!configuredTransport) services.AddHttpClient<MoneyPuckService>().ConfigurePrimaryHttpMessageHandler(() => Handler).SetHandlerLifetime(Timeout.InfiniteTimeSpan);
                configureServices?.Invoke(services);
            });
        });
    }
    public MoneyPuckQualificationService Service => App.Services.GetRequiredService<MoneyPuckQualificationService>();
    public async Task<List<TeamRating>> RowsAsync(string source = Source)
    {
        using var scope = App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().TeamRatings.AsNoTracking().Where(r => r.Source == source).OrderBy(r => r.TeamAbbreviation).ToListAsync();
    }
    public async Task<PlayoffRatingPolicy.Selection> SelectAsync(string team = "NSH")
    {
        var rows = await RowsAsync();
        var request = new PlayoffRatingPolicy.SelectionRequest("nhl", 2027, team, Clock.Now.UtcDateTime, Options);
        return PlayoffRatingPolicy.select(request, rows);
    }
    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (App is not null) await App.DisposeAsync();
        await entryPoint.DisposeAsync();
        Handler.Dispose();
        await database.DisposeAsync();
    }
}
