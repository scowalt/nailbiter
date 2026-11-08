using backend.Data;
using backend.GraphQL.Types;
using backend.Models;
using backend.Services;
using backend.Services.Backfill;
using backend.Services.Espn;
using backend.Services.Kalshi;
using backend.Services.EspnIndyCar;
using backend.Services.EspnNascar;
using backend.Services.F1;
using backend.Services.IndyCar;
using backend.Services.Nascar;
using backend.Services.EspnF1;
using backend.Services.OpenF1;
using backend.Services.PlayoffStatus;
using backend.Services.PowerRankingsGuru;
using backend.Routes;
using Backend.Rendering;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Polly.Telemetry;
using StarFederation.Datastar.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseSentry(o =>
{
    o.Dsn = builder.Configuration["Sentry:Dsn"];
    o.SendDefaultPii = builder.Configuration.GetValue<bool>("Sentry:SendDefaultPii");
    o.TracesSampleRate = builder.Configuration.GetValue<double>("Sentry:TracesSampleRate");

    o.SetBeforeSend((sentryEvent, hint) =>
    {
        if (sentryEvent.Message?.Message?.Contains("Resilience event occurred") == true)
            return null;  

        if (sentryEvent.Extra?.TryGetValue("eventId", out var eventId) == true &&
            eventId?.ToString() == "ResilienceEvent")
            return null;  

        if (sentryEvent.Logger == DbLoggerCategory.Database.Command.Name
            || sentryEvent.Logger == DbLoggerCategory.Database.Transaction.Name)
            return null;

        return sentryEvent;
    });
});

builder.Services.Configure<TelemetryOptions>(options => options.SeverityProvider = args =>
    args.Event.Severity == ResilienceEventSeverity.Error ? ResilienceEventSeverity.Warning : args.Event.Severity);

builder.Services.AddOpenApi();

builder.Services.AddDatastar();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(
                "https://www.nailbiter.app",
                "https://live.nailbiter.app",
                "http://localhost:5173",
                "http://localhost:5174"
              )
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

if (builder.Configuration.GetValue<bool>("OpenTelemetry:Enabled"))
{
    var otelEndpoint = builder.Configuration["OpenTelemetry:Endpoint"] ?? "http://localhost:4317";

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService("nailbiter-backend"))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddEntityFrameworkCoreInstrumentation()
            .AddSource("Npgsql")

            .AddSource("Nailbiter.HypeScore")
            .AddSource("Nailbiter.Resolvers")
            .AddOtlpExporter(opts => opts.Endpoint = new Uri(otelEndpoint)));
}

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Database=excitement_score;Username=postgres;Password=postgres";

builder.Services.AddDbContext<ExcitementDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddSingleton<IGameUpdateService, GameUpdateService>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<backend.Services.Highlights.YouTubeHighlightsOptions>(builder.Configuration.GetSection("Highlights:YouTube"));
builder.Services.AddHttpClient("YouTubeHighlights")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
    .RemoveAllLoggers().RedactLoggedHeaders(["x-goog-api-key"]);
builder.Services.AddSingleton<backend.Services.Highlights.YouTubeHighlightsClient>();
builder.Services.AddHttpClient(backend.Services.Highlights.MlbHighlightsIdentityClient.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false }).RemoveAllLoggers();
builder.Services.AddScoped<backend.Services.Highlights.MlbHighlightsIdentityClient>();
builder.Services.AddScoped<backend.Services.Highlights.HighlightsApplicationService>();
builder.Services.AddScoped<backend.Services.Highlights.HighlightsOperatorCommand>();
builder.Services.Configure<backend.Services.Highlights.HighlightsWorkerOptions>(builder.Configuration.GetSection("Highlights:Worker"));
builder.Services.AddScoped<backend.Services.Highlights.HighlightsLifecycleService>();
builder.Services.AddHostedService<backend.Services.Highlights.HighlightsBackgroundService>();
builder.Services.Configure<PlayoffRatingsOptions>(builder.Configuration.GetSection("PlayoffRatings"));
builder.Services.AddScoped<PlayoffRatingPolicy.PlayoffSelectionCache>();
builder.Services.AddSingleton<PlayoffStatusNhlRefreshGate>();
builder.Services.AddSingleton<MoneyPuckRefreshGate>();

builder.Services.AddScoped<GameDataService>();
builder.Services.AddScoped<GameHighlightsInspectionService>();

builder.Services.AddSingleton<ISseBroadcaster, SseBroadcaster>();

builder.Services.AddEspnHttpClient<EspnOddsService>();

builder.Services.AddHttpClient<KalshiOddsService>()
    .AddHttpMessageHandler(() => new KalshiAccessRefusalHandler())
    .AddStandardResilienceHandler();

builder.Services.AddMemoryCache();
builder.Services.AddEspnHttpClient<CollegeEspnClient>();
builder.Services.AddHttpClient<CollegeKalshiClient>()
    .AddHttpMessageHandler(() => new KalshiAccessRefusalHandler())
    .AddStandardResilienceHandler();
builder.Services.AddScoped<CollegeFootballService>();

builder.Services.AddScoped<PregameOddsService>();

builder.Services.AddEspnHttpClient<EspnPowerIndexService>();
builder.Services.AddHttpClient(nameof(PlayoffStatusService))
    .AddTypedClient((http, services) => new PlayoffStatusService(http,
        services.GetRequiredService<ILogger<PlayoffStatusService>>(), services,
        services.GetRequiredService<TimeProvider>(),
        services.GetRequiredService<Microsoft.Extensions.Options.IOptions<PlayoffRatingsOptions>>(),
        services.GetRequiredService<PlayoffStatusNhlRefreshGate>()))
    .AddStandardResilienceHandler();
builder.Services.AddHttpClient<PowerRankingsGuruService>()
    .AddStandardResilienceHandler();
builder.Services.AddHttpClient<NhlPowerRankingsGuruService>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Nailbiter-NHL-Rankings/1.0 (+https://nailbiter.app)");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    });
builder.Services.AddHttpClient<MoneyPuckService>(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddTransient<MoneyPuckQualificationService>();
builder.Services.AddHttpClient<KalshiPlayoffRatingsService>()
    .AddHttpMessageHandler(() => new KalshiAccessRefusalHandler())
    .AddStandardResilienceHandler();
builder.Services.AddScoped<HypeScoreService>();
builder.Services.AddHttpClient<NflPlayoffLeverageCollector>(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Nailbiter-NFL-Leverage/1.0 (+https://nailbiter.app)");
        client.Timeout = TimeSpan.FromSeconds(15);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    });
builder.Services.AddScoped<ProHypeLifecycle>();
builder.Services.AddScoped<IProHypeLifecycle>(services => services.GetRequiredService<ProHypeLifecycle>());
builder.Services.AddEspnHttpClient<ProCompetitionEvidenceService>();

builder.Services.AddEspnHttpClient<EspnStandingsService>();

builder.Services.AddEspnHttpClient<NflBackfillService>();
builder.Services.AddScoped<RetrospectiveEloService>();

builder.Services.AddHttpClient<OpenF1Service>()
    .AddStandardResilienceHandler();
builder.Services.AddEspnHttpClient<EspnF1Service>();
builder.Services.AddScoped<F1DataService>();
builder.Services.AddScoped<F1LiveDataService>();

builder.Services.AddHttpClient<KalshiF1OddsService>()
    .AddStandardResilienceHandler();

builder.Services.AddEspnHttpClient<EspnNascarService>();
builder.Services.AddHttpClient<KalshiNascarOddsService>()
    .AddStandardResilienceHandler();
builder.Services.AddHttpClient<NascarLiveFeedService>()
    .AddStandardResilienceHandler();
builder.Services.AddScoped<NascarDataService>();
builder.Services.AddScoped<NascarLiveDataService>();

builder.Services.AddEspnHttpClient<EspnIndyCarService>();
builder.Services.AddScoped<IndyCarDataService>();
builder.Services.AddScoped<IndyCarLiveDataService>();

if (!builder.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("EnableScraper")
    || builder.Configuration.GetValue<bool>("EnableProHypeCapture"))
    builder.Services.AddHostedService<ProHypeCaptureBackgroundService>();

if (!builder.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("EnableScraper"))
{
    builder.Services.AddHostedService<OddsScrapeBackgroundService>();
    builder.Services.AddHostedService<CollegeFootballBackgroundService>();
    builder.Services.AddHostedService<RatingsRefreshBackgroundService>();
    builder.Services.AddHostedService<ProCompetitionRefreshBackgroundService>();
    builder.Services.AddHostedService<KalshiPlayoffRefreshBackgroundService>();
    builder.Services.AddHostedService<PlayoffStatusNhlRefreshBackgroundService>();
    builder.Services.AddHostedService<MoneyPuckRefreshBackgroundService>();
    builder.Services.AddHostedService<NascarBackgroundService>();
    builder.Services.AddHostedService<IndyCarBackgroundService>();
    builder.Services.AddHostedService<F1BackgroundService>();
}

var app = builder.Build();

var highlightsCommandIndex = Array.IndexOf(args, "--highlights");
if (highlightsCommandIndex >= 0)
{
    using var scope = app.Services.CreateScope();
    var command = scope.ServiceProvider.GetRequiredService<backend.Services.Highlights.HighlightsOperatorCommand>();
    Environment.ExitCode = await command.RunAsync(args.Skip(highlightsCommandIndex + 1).ToArray(), Console.Out);
    return;
}

if (args.Contains("--import-college-history"))
{
    using var scope = app.Services.CreateScope();
    var importer = scope.ServiceProvider.GetRequiredService<CollegeFootballService>();
    var count = await importer.RunAsync(initialHistory: true, apply: args.Contains("--apply"), CancellationToken.None);
    Console.WriteLine($"College initial import: {count} eligible games ({(args.Contains("--apply") ? "committed" : "dry run")}).");
    return;
}

if (Environment.GetEnvironmentVariable("BACKFILL_MODE") == "true")
{
    var seasonYear = int.Parse(Environment.GetEnvironmentVariable("BACKFILL_SEASON") ?? "2024");
    var dryRun = Environment.GetEnvironmentVariable("BACKFILL_DRY_RUN") == "true";
    var calculateElo = Environment.GetEnvironmentVariable("BACKFILL_ELO") == "true";
    var backfillKalshi = Environment.GetEnvironmentVariable("BACKFILL_KALSHI") == "true";
    var kalshiInterval = int.TryParse(Environment.GetEnvironmentVariable("BACKFILL_KALSHI_INTERVAL"), out var ki) ? ki : 60;
    var seasonType = Environment.GetEnvironmentVariable("BACKFILL_SEASON_TYPE") is string st ? int.Parse(st) : (int?)null;
    var startWeek = Environment.GetEnvironmentVariable("BACKFILL_START_WEEK") is string sw ? int.Parse(sw) : (int?)null;
    var endWeek = Environment.GetEnvironmentVariable("BACKFILL_END_WEEK") is string ew ? int.Parse(ew) : (int?)null;

    using var scope = app.Services.CreateScope();
    var backfillService = scope.ServiceProvider.GetRequiredService<NflBackfillService>();

    var options = new BackfillOptions
    {
        DryRun = dryRun,
        CalculateElo = calculateElo,
        BackfillKalshiOdds = backfillKalshi,
        KalshiCandlestickInterval = kalshiInterval,
        SeasonType = seasonType,
        StartWeek = startWeek,
        EndWeek = endWeek,
        SkipExisting = !dryRun  
    };

    Console.WriteLine($"Starting NFL {seasonYear} backfill...");
    Console.WriteLine($"  DryRun: {dryRun}");
    Console.WriteLine($"  CalculateElo: {calculateElo}");
    Console.WriteLine($"  BackfillKalshi: {backfillKalshi} (interval: {kalshiInterval}min)");
    Console.WriteLine($"  SeasonType: {seasonType?.ToString() ?? "all"}");
    Console.WriteLine($"  Weeks: {startWeek?.ToString() ?? "1"} - {endWeek?.ToString() ?? "all"}");

    var result = await backfillService.BackfillSeasonAsync(seasonYear, options, null);

    Console.WriteLine();
    Console.WriteLine("=== Backfill Complete ===");
    Console.WriteLine($"  Games Created: {result.GamesCreated}");
    Console.WriteLine($"  Games Updated: {result.GamesUpdated}");
    Console.WriteLine($"  Games Skipped: {result.GamesSkipped}");
    Console.WriteLine($"  Pregame Odds: {result.PregameOddsCreated}");
    Console.WriteLine($"  Kalshi Odds Snapshots: {result.KalshiOddsSnapshotsCreated}");
    Console.WriteLine($"  Team Ratings: {result.TeamRatingsCreated}");
    Console.WriteLine($"  Duration: {result.Duration:g}");

    if (result.Errors.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"  Errors ({result.Errors.Count}):");
        foreach (var error in result.Errors.Take(10))
        {
            Console.WriteLine($"    - {error}");
        }
        if (result.Errors.Count > 10)
        {
            Console.WriteLine($"    ... and {result.Errors.Count - 10} more");
        }
    }

    return;  
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        if (!context.Response.HasStarted)
            context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
    }
});

app.UseCors();
app.UseStaticFiles();
app.UseHttpsRedirection();

app.MapPageRoutes();
app.MapMotorsportRoutes();
app.MapSseRoutes();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.MapPost("/api/scrape/{sport}", async (string sport, EspnOddsService espnService) =>
{
    var snapshots = await espnService.ScrapeAndStoreOddsAsync(sport, null);
    return Results.Ok(new
    {
        sport,
        gamesScraped = snapshots.Count,
        snapshots = snapshots.Select(s => new
        {
            s.GameId,
            s.Timestamp,
            s.HomeWinProbability,
            s.AwayWinProbability,
            s.HomeMoneyline,
            s.AwayMoneyline
        })
    });
});

app.MapPost("/api/f1/refresh/{year:int}", async (int year, F1DataService f1Service, CancellationToken ct) =>
{
    var racesAdded = await f1Service.RefreshScheduleAsync(year, ct);
    var driversAdded = await f1Service.RefreshDriversAsync(year, ct);
    return Results.Ok(new
    {
        year,
        racesAdded,
        driversAdded
    });
});

app.MapPost("/api/nascar/refresh/{year:int}", async (int year, NascarDataService nascarService, CancellationToken ct) =>
{
    var racesAdded = await nascarService.RefreshScheduleAsync(year, ct);
    var driversAdded = await nascarService.RefreshDriversAsync(year, ct);
    var resultsAdded = await nascarService.RefreshRaceResultsAsync(year, ct);
    return Results.Ok(new
    {
        year,
        racesAdded,
        driversAdded,
        resultsAdded
    });
});

app.MapPost("/api/indycar/refresh/{year:int}", async (int year, IndyCarDataService indyCarService, CancellationToken ct) =>
{
    var racesAdded = await indyCarService.RefreshScheduleAsync(year, ct);
    var driversAdded = await indyCarService.RefreshDriversAsync(year, ct);
    var resultsAdded = await indyCarService.RefreshRaceResultsAsync(year, ct);
    return Results.Ok(new
    {
        year,
        racesAdded,
        driversAdded,
        resultsAdded
    });
});

app.MapPost("/api/ratings/refresh", async (
    PowerRankingsGuruService powerRankingsGuru,
    MoneyPuckService moneyPuck,
    EspnStandingsService standings,
    PlayoffStatusService playoffStatus,
    KalshiPlayoffRatingsService kalshiPlayoffs,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var results = new Dictionary<string, object>();

    async Task Run(string key, Func<Task<int>> action)
    {
        try { results[key] = await action(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ratings refresh step {Step} failed", key);
            results[key] = $"error: {ex.Message}";
        }
    }

    await Run("kalshiPlayoffs", () => kalshiPlayoffs.RefreshCurrentAsync(ct));
    var moneyPuckResult = await moneyPuck.RefreshNhlAsync(ct);
    results["nhl_moneypuckQualification"] = moneyPuckResult.Qualification;
    results["nhl_powerRankings"] = moneyPuckResult.Rankings;
    await Run("nfl_powerRankings", () => powerRankingsGuru.RefreshNflRatingsAsync(ct));
    await Run("nba_powerRankings", () => powerRankingsGuru.RefreshNbaRatingsAsync(ct));
    await Run("nhl_guruPowerRankings", () => powerRankingsGuru.RefreshNhlRatingsAsync(ct));
    await Run("mlb_powerRankings", () => powerRankingsGuru.RefreshMlbRatingsAsync(ct));

    await Run("nfl_standings", () => standings.RefreshNflStandingsAsync(ct));
    await Run("nba_standings", () => standings.RefreshNbaStandingsAsync(ct));
    await Run("nhl_standings", () => standings.RefreshNhlStandingsAsync(ct));
    await Run("mlb_standings", () => standings.RefreshMlbStandingsAsync(ct));

    await Run("nfl_playoffStatus", () => playoffStatus.RefreshNflRatingsAsync(ct));
    await Run("nba_playoffStatus", () => playoffStatus.RefreshNbaRatingsAsync(ct));
    await Run("nhl_playoffStatus", () => playoffStatus.RefreshNhlRatingsAsync(ct));
    await Run("mlb_playoffStatus", () => playoffStatus.RefreshMlbRatingsAsync(ct));

    return Results.Ok(results);
});

app.MapGet("/api/games/{gameId}/odds", async (string gameId, ExcitementDbContext db, CancellationToken ct) =>
{
    var game = await db.Games
        .Where(GameVisibilityPolicy.Visible)
        .Where(g => g.Id == gameId)
        .Select(g => new { g.Id, g.HomeTeam, g.AwayTeam, g.Status, g.IsHistoricalImport })
        .FirstOrDefaultAsync(ct);

    if (game == null)
        return Results.NotFound();

    var snapshots = await db.OddsSnapshots
        .Where(o => o.GameId == gameId)
        .OrderByDescending(o => o.Timestamp)
        .Take(100)
        .Select(o => new
        {
            o.Timestamp,
            o.Source,
            o.IsHistorical,
            isProbabilityBound = GameWinnerProbabilityPolicy.IsBound(o.Source),
            o.HomeWinProbability,
            o.AwayWinProbability,
            o.HomeScore,
            o.AwayScore,
            o.Period,
            o.GameClock
        })
        .ToListAsync(ct);

    return Results.Ok(new
    {
        game.Id,
        game.HomeTeam,
        game.AwayTeam,
        game.Status,
        game.IsHistoricalImport,
        oddsHistory = snapshots.OrderBy(o => o.Timestamp)
    });
});

app.MapGet("/api/games", async (ExcitementDbContext db, TimeProvider time, string? sport, string? status) =>
{
    var query = db.Games.Where(GameVisibilityPolicy.Visible);

    if (!string.IsNullOrEmpty(sport))
        query = query.Where(g => g.Sport == sport.ToLower());

    if (!string.IsNullOrEmpty(status))
        query = query.Where(g => g.Status == status.ToLower());

    var now = time.GetUtcNow().UtcDateTime;
    var freshCutoff = now.AddMinutes(-2);
    var games = await query
        .OrderByDescending(g => g.ScheduledStart)
        .Take(50)
        .Select(g => new
        {
            g.Id,
            g.Sport,
            g.HomeTeam,
            g.AwayTeam,
            g.HomeTeamAbbreviation,
            g.AwayTeamAbbreviation,
            g.Status,
            g.ScheduledStart,
            g.HomeScore,
            g.AwayScore,
            g.Period,
            g.GameClock,
            g.IsHistoricalImport,
            latestOdds = g.OddsSnapshots
                .Where(o => (o.Source != GameWinnerProbabilityPolicy.OneSidedSource && o.Source != GameWinnerProbabilityPolicy.ResolvedSource)
                    || (o.Timestamp > freshCutoff && o.Timestamp <= now))
                .Where(o => g.Sport != "cfb" || (!o.IsHistorical && o.Timestamp > freshCutoff
                    && (g.MarketObservedAt == null || o.Timestamp >= g.MarketObservedAt)))
                .OrderByDescending(o => o.Timestamp)
                .Select(o => new { o.HomeWinProbability, o.AwayWinProbability, o.Source,
                    isProbabilityBound = GameWinnerProbabilityPolicy.IsBound(o.Source) })
                .FirstOrDefault()
        })
        .ToListAsync();

    return Results.Ok(games);
});

app.Run();

public partial class Program { }
