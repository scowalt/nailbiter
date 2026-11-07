using System.Data.Common;
using System.Net;
using backend.Data;
using backend.Models;
using backend.Scoring;
using backend.Services;
using backend.Services.Kalshi;
using backend.Services.PlayoffStatus;
using backend.Services.PowerRankingsGuru;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace backend.tests;

public sealed partial class NflPlayoffPageTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer database = IsolatedPostgreSql.CreateContainer();
    private readonly AppFactory entryPoint = new();
    private readonly Clock clock = new();
    private WebApplicationFactory<Program> app = null!;
    private HttpClient client = null!;

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 13, 18, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public async Task InitializeAsync()
    {
        await database.StartAsync();
        app = entryPoint.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
            services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
            services.AddDbContext<ExcitementDbContext>(options => options.UseNpgsql(database.GetConnectionString()));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));
        client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "035_ProHypeLifecycle.sql")));
        db.Games.Add(new Game
        {
            Id = "nfl-current", Sport = "nfl", HomeTeam = "Kansas City Chiefs", AwayTeam = "Buffalo Bills",
            HomeTeamAbbreviation = "KC", AwayTeamAbbreviation = "BUF", SeasonYear = 2026,
            SeasonType = 2, WeekNumber = 1, Status = "scheduled", ScheduledStart = clock.Now.UtcDateTime
        });
        db.PregameOdds.Add(new PregameOdds { GameId = "nfl-current", HomeWinProbability = 0.5, AwayWinProbability = 0.5 });
        foreach (var team in new[] { "KC", "BUF" })
        {
            db.TeamRatings.Add(new TeamRating
            {
                Sport = "nfl", TeamAbbreviation = team, Source = "powerrankingsguru", Season = 2026,
                PowerRank = 18, PlayoffProbability = 0.005, FetchedAt = clock.Now.UtcDateTime
            });
            db.TeamRatings.Add(new TeamRating
            {
                Sport = "nfl", TeamAbbreviation = team, Source = "kalshi", Season = 2026,
                SourceSeason = 2026, PlayoffProbability = 0.635, FetchedAt = clock.Now.UtcDateTime
            });
        }
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/nfl?week=1&season=2026&type=2")]
    [InlineData("/game/nfl-current")]
    public async Task Pages_use_Kalshi_then_show_unavailable_after_expiry(string path)
    {
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var text = await BodyText(response);
        Assert.Matches(@"(?<!\d)64\s*%", text);
        Assert.DoesNotContain("18th in NFL", text);
        Assert.DoesNotContain("#18", text);
        Assert.DoesNotMatch(@"(?<!\d)1\s*% playoff odds", text);

        clock.Now = clock.Now.AddHours(1);
        response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        text = await BodyText(response);
        Assert.Contains("unavailable", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"(?<!\d)64\s*%", text);
    }

    [Fact]
    public async Task Pro_metrics_share_one_policy_across_public_service_home_league_and_detail()
    {
        clock.Now = new DateTimeOffset(ProHypeTests.Now);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var hype = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
        foreach (var sport in new[] { "nhl", "nba", "nfl", "mlb" })
        {
            var game = ProHypeTests.Game(sport); game.Id = "pro-" + sport; game.WeekNumber = 1;
            var evidence = ProHypeTests.Evidence(game);
            if (sport == "nfl") evidence.NflPlayoffLeverage = NflPlayoffLeverageTests.SyntheticEvidence(game, clock.Now.UtcDateTime);
            game.ProEvidenceJson = System.Text.Json.JsonSerializer.Serialize(evidence);
            var ranks = ProHypeTests.Ranks(game);
            var qualification = new[] { game.HomeTeamAbbreviation, game.AwayTeamAbbreviation }.Select(team => new TeamRating
            {
                Sport = sport, TeamAbbreviation = team, Source = "kalshi", Season = game.SeasonYear!.Value,
                SourceSeason = game.SeasonYear, PlayoffProbability = .5, FetchedAt = clock.Now.UtcDateTime
            }).ToArray();
            var odds = ProHypeTests.Odds(game);
            db.Games.Add(game); db.TeamRatings.AddRange(ranks); db.TeamRatings.AddRange(qualification); db.PregameOdds.Add(odds);
            await db.SaveChangesAsync();
            var stored = await hype.GetHypeComponentsAsync(game, null);
            var direct = hype.CalculateFromEvidence(game, ranks.Concat(qualification), odds, Array.Empty<string>(), clock.Now.UtcDateTime);
            Assert.True(stored.IsComplete); Assert.Equal(direct.StakesScore, stored.StakesScore); Assert.Equal(direct.QualityScore, stored.QualityScore);
            var score = await hype.CalculateHypeScoreAsync(game, null);
            Assert.Equal(hype.CalculateFinalScore(sport, direct), score);
            var detail = await BodyText(await client.GetAsync("/game/" + game.Id));
            Assert.Contains(sport == "nhl" ? "moneypuck" : "powerrankingsguru", stored.QualityExplanation); Assert.Contains("2026-09-30", stored.QualityExplanation);
            Assert.Contains("Closeness100", detail); Assert.DoesNotContain("Hype unavailable", detail);
            db.PregameOdds.Remove(odds); await db.SaveChangesAsync();
            detail = await BodyText(await client.GetAsync("/game/" + game.Id));
            Assert.Contains("Hype unavailable", detail); Assert.Contains("ClosenessUnavailable", detail); Assert.DoesNotContain("QualityUnavailable", detail);
            foreach (var path in new[] { "/", $"/{sport}?date=2026-09-30&week=1&season={game.SeasonYear}&type=2" })
            {
                using var doc = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(await client.GetStringAsync(path), CancellationToken.None);
                Assert.Contains("Hype unavailable", doc.QuerySelector($"a[href='/game/{game.Id}']")!.TextContent);
            }
            var final = ProHypeTests.Game(sport); final.Id = "new-final-" + sport; final.Status = "final";
            db.Games.Add(final); await db.SaveChangesAsync();
            stored = await hype.GetHypeComponentsAsync(game, null);
            Assert.False(stored.StakesAvailable); Assert.True(stored.QualityAvailable);
            Assert.Contains("known completed result", stored.StakesExplanation);
        }
    }

    [Fact]
    public async Task NHL_model_rows_are_default_off_then_selected_without_source_UI_when_enabled()
    {
        clock.Now = new DateTimeOffset(ProHypeTests.Now);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var game = ProHypeTests.Game();
        game.Id = "nhl-model-fallback";
        game.ProEvidenceJson = System.Text.Json.JsonSerializer.Serialize(ProHypeTests.Evidence(game));
        db.Games.Add(game);
        db.PregameOdds.Add(ProHypeTests.Odds(game));
        db.TeamRatings.AddRange(ProHypeTests.Ranks(game));
        foreach (var (team, probability) in new[] { (game.HomeTeamAbbreviation, .51), (game.AwayTeamAbbreviation, .72) })
        {
            db.TeamRatings.Add(new TeamRating { Sport = "nhl", TeamAbbreviation = team, Season = 2027, SourceSeason = 2027,
                Source = "kalshi", PlayoffProbability = null, FetchedAt = clock.Now.UtcDateTime });
            db.TeamRatings.Add(ModelRow(team, probability, ProHypeTests.Now.AddHours(-24), ProHypeTests.Now));
        }
        await db.SaveChangesAsync();

        var data = scope.ServiceProvider.GetRequiredService<GameDataService>();
        var rank = await data.GetTeamRankInfoAsync("nhl", game.HomeTeamAbbreviation, 2027, 2, CancellationToken.None);
        Assert.NotNull(rank);
        Assert.Null(rank.Value.PlayoffOdds);
        var hype = await scope.ServiceProvider.GetRequiredService<HypeScoreService>().GetHypeComponentsAsync(game, null);
        Assert.False(hype.HomePlayoffOdds.HasValue);

        await using var enabledApp = CreateAppWithSettings(new Dictionary<string, string?>
        {
            ["PlayoffRatings:NhlModelMode"] = "fallback",
            ["PlayoffRatings:ApprovedSeasons:0"] = "2027"
        });
        using var enabledClient = enabledApp.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using (var enabledScope = enabledApp.Services.CreateScope())
        {
            data = enabledScope.ServiceProvider.GetRequiredService<GameDataService>();
            rank = await data.GetTeamRankInfoAsync("nhl", game.HomeTeamAbbreviation, 2027, 2, CancellationToken.None);
            Assert.NotNull(rank);
            var enabledRank = rank.Value;
            Assert.NotNull(enabledRank.PlayoffOdds);
            Assert.Equal(.51, enabledRank.PlayoffOdds.Value, 6);
            hype = await enabledScope.ServiceProvider.GetRequiredService<HypeScoreService>().GetHypeComponentsAsync(game, null);
            Assert.Equal(.51, hype.HomePlayoffOdds.GetValueOrDefault(), 6);
            Assert.Equal(.72, hype.AwayPlayoffOdds.GetValueOrDefault(), 6);
        }

        foreach (var path in new[] { "/", "/nhl?date=2026-09-30", "/game/" + game.Id })
        {
            var html = await enabledClient.GetStringAsync(path);
            AssertNoScoringDiagnostics(html);
            using var doc = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(html, CancellationToken.None);
            var text = path.StartsWith("/game/", StringComparison.Ordinal)
                ? doc.Body!.TextContent
                : doc.QuerySelector($"a[href='/game/{game.Id}']")!.TextContent;
            Assert.DoesNotContain("Polymarket", text);
            Assert.DoesNotContain("PlayoffStatus", text);
            Assert.DoesNotContain("playoffstatus", text);
            Assert.DoesNotContain("model", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Kalshi", text);
            Assert.DoesNotContain("Missed playoffs", text);
            AssertContainsPercent(51, text);
            AssertContainsPercent(72, text);
        }
    }

    [Fact]
    public async Task Current_request_selection_cache_remains_consistent_independently_of_resolved_page_hype()
    {
        clock.Now = new DateTimeOffset(ProHypeTests.Now);
        var race = new RefreshRaceInterceptor(database.GetConnectionString());
        await using var enabledApp = CreateAppWithSettings(new Dictionary<string, string?>
        {
            ["PlayoffRatings:NhlModelMode"] = "fallback",
            ["PlayoffRatings:ApprovedSeasons:0"] = "2027"
        }, options => options.AddInterceptors(race));
        using (var seedScope = enabledApp.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var game = ProHypeTests.Game();
            game.Id = "nhl-same-request-cache";
            game.ProEvidenceJson = System.Text.Json.JsonSerializer.Serialize(ProHypeTests.Evidence(game));
            db.Games.Add(game);
            db.PregameOdds.Add(ProHypeTests.Odds(game));
            db.TeamRatings.AddRange(ProHypeTests.Ranks(game));
            db.TeamRatings.Add(ModelRow(game.HomeTeamAbbreviation, .51, ProHypeTests.Now.AddHours(-24), ProHypeTests.Now));
            db.TeamRatings.Add(ModelRow(game.AwayTeamAbbreviation, .72, ProHypeTests.Now.AddHours(-24), ProHypeTests.Now));
            await db.SaveChangesAsync();
        }

        using var enabledClient = enabledApp.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        race.Arm();
        using (var requestScope = enabledApp.Services.CreateScope())
        {
            var db = requestScope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var game = await db.Games.SingleAsync(g => g.Id == "nhl-same-request-cache");
            var hype = await requestScope.ServiceProvider.GetRequiredService<HypeScoreService>().GetHypeComponentsAsync(game, null);
            Assert.True(hype.HomePlayoffOdds.HasValue);
            Assert.True(hype.AwayPlayoffOdds.HasValue);
            Assert.Equal(.51, hype.HomePlayoffOdds.GetValueOrDefault(), 6);
            Assert.Equal(.72, hype.AwayPlayoffOdds.GetValueOrDefault(), 6);
            Assert.True(hype.IsComplete);
            var data = requestScope.ServiceProvider.GetRequiredService<GameDataService>();
            Assert.Equal(.51, (await data.GetTeamRankInfoAsync("nhl", "PHI", 2027, 2, default)).Value.PlayoffOdds.Value, 6);
            Assert.Equal(.72, (await data.GetTeamRankInfoAsync("nhl", "PIT", 2027, 2, default)).Value.PlayoffOdds.Value, 6);
        }

        using (var verifyScope = enabledApp.Services.CreateScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var home = await db.TeamRatings.AsNoTracking().SingleAsync(r => r.Source == "playoffstatus" && r.TeamAbbreviation == "PHI");
            Assert.Null(home.PlayoffProbability);
        }

        var text = await BodyText(await enabledClient.GetAsync("/game/nhl-same-request-cache"));
        Assert.DoesNotMatch(@"(?<!\d)51\s*%", text);
        Assert.Contains("Unavailable", text);
    }

    [Fact]
    public void Public_hype_service_requires_verified_row_evidence_for_model_and_final_qualification()
    {
        clock.Now = new DateTimeOffset(ProHypeTests.Now);
        var game = ProHypeTests.Game();
        game.ProEvidenceJson = System.Text.Json.JsonSerializer.Serialize(ProHypeTests.Evidence(game));
        var odds = ProHypeTests.Odds(game);
        var service = new HypeScoreService(app.Services, Microsoft.Extensions.Logging.Abstractions.NullLogger<HypeScoreService>.Instance, clock,
            Microsoft.Extensions.Options.Options.Create(new PlayoffRatingsOptions { NhlModelMode = "fallback", ApprovedSeasons = [2027] }), null!);
        Assert.Null(typeof(backend.Scoring.ProHypePolicy).GetMethod("Calculate", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
        Assert.False(typeof(backend.Scoring.ProHypePolicy).Assembly.GetType("backend.Scoring.ProQualification")!.IsPublic);

        var legacyRows = ProHypeTests.Ranks(game).Concat(new[]
        {
            new TeamRating { Sport = "nhl", TeamAbbreviation = game.HomeTeamAbbreviation, Season = 2027, SourceSeason = 2027,
                Source = PlayoffRatingPolicy.playoffStatusSource, PlayoffProbability = .51, FetchedAt = ProHypeTests.Now },
            new TeamRating { Sport = "nhl", TeamAbbreviation = game.AwayTeamAbbreviation, Season = 2027, SourceSeason = 2027,
                Source = PlayoffRatingPolicy.playoffStatusSource, PlayoffProbability = .72, FetchedAt = ProHypeTests.Now }
        });
        var legacy = service.CalculateFromEvidence(game, legacyRows, odds, Array.Empty<string>(), ProHypeTests.Now);
        Assert.False(legacy.HomePlayoffOdds.HasValue);
        Assert.False(legacy.StakesAvailable);

        var forgedFinalRows = ProHypeTests.Ranks(game).Concat(new[]
        {
            new TeamRating { Sport = "nhl", TeamAbbreviation = game.HomeTeamAbbreviation, Season = 2027, SourceSeason = 2027,
                Source = PlayoffRatingPolicy.kalshiSource, PlayoffProbability = 0, PlayoffProbabilityIsFinal = true, FetchedAt = ProHypeTests.Now.AddYears(-1) },
            new TeamRating { Sport = "nhl", TeamAbbreviation = game.AwayTeamAbbreviation, Season = 2027, SourceSeason = 2027,
                Source = PlayoffRatingPolicy.kalshiSource, PlayoffProbability = 0, PlayoffProbabilityIsFinal = true, FetchedAt = ProHypeTests.Now.AddYears(-1) }
        });
        var forgedFinal = service.CalculateFromEvidence(game, forgedFinalRows, odds, Array.Empty<string>(), ProHypeTests.Now);
        Assert.False(forgedFinal.HomePlayoffOdds.HasValue);
        Assert.False(forgedFinal.StakesAvailable);

        var expiredModelRows = ProHypeTests.Ranks(game).Concat(new[]
        {
            ModelRow(game.HomeTeamAbbreviation, .51, ProHypeTests.Now.AddHours(-37), ProHypeTests.Now),
            ModelRow(game.AwayTeamAbbreviation, .72, ProHypeTests.Now.AddHours(-37), ProHypeTests.Now)
        });
        var expiredModel = service.CalculateFromEvidence(game, expiredModelRows, odds, Array.Empty<string>(), ProHypeTests.Now);
        Assert.False(expiredModel.HomePlayoffOdds.HasValue);
        Assert.False(expiredModel.StakesAvailable);

        var validatedModelRows = ProHypeTests.Ranks(game).Concat(new[]
        {
            ModelRow(game.HomeTeamAbbreviation, .51, ProHypeTests.Now.AddHours(-24), ProHypeTests.Now),
            ModelRow(game.AwayTeamAbbreviation, .72, ProHypeTests.Now.AddHours(-24), ProHypeTests.Now)
        });
        var validated = service.CalculateFromEvidence(game, validatedModelRows, odds, Array.Empty<string>(), ProHypeTests.Now);
        Assert.Equal(.51, validated.HomePlayoffOdds.GetValueOrDefault(), 6);
        Assert.Equal(.72, validated.AwayPlayoffOdds.GetValueOrDefault(), 6);
        Assert.True(validated.StakesAvailable, validated.StakesExplanation);
    }

    [Fact]
    public async Task Wide_Columbus_quote_keeps_hype_unavailable_without_sending_raw_diagnostics()
    {
        clock.Now = new DateTimeOffset(ProHypeTests.Now);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var game = ProHypeTests.Game(); game.Id = "cbj-wide-quote";
        game.HomeTeam = "Columbus Blue Jackets"; game.HomeTeamAbbreviation = "CBJ";
        game.AwayTeam = "Buffalo Sabres"; game.AwayTeamAbbreviation = "BUF";
        var evidence = ProHypeTests.Evidence(game);
        evidence.Source = "SERVER_ONLY_COMPETITION_EVIDENCE";
        game.ProEvidenceJson = System.Text.Json.JsonSerializer.Serialize(evidence);
        db.Games.Add(game); db.TeamRatings.AddRange(ProHypeTests.Ranks(game, 15, 12));
        db.PregameOdds.Add(ProHypeTests.Odds(game, .505));
        await db.SaveChangesAsync();

        var cbjAsk = "0.8000";
        using var handler = new ProFixtureHandler(url =>
        {
            var body = url.Contains("/historical/") ? "{\"markets\":[],\"cursor\":\"\"}" : System.Text.Json.JsonSerializer.Serialize(new
            {
                @event = new
                {
                    event_ticker = "KXNHLPLAYOFF-27", series_ticker = "KXNHLPLAYOFF", sub_title = "2026-27",
                    markets = new[] { ("CBJ", "0.2000", cbjAsk), ("BUF", "0.5500", "0.5900") }.Select(x => new
                    {
                        ticker = "KXNHLPLAYOFF-27-" + x.Item1, event_ticker = "KXNHLPLAYOFF-27",
                        status = "active", result = "", market_type = "binary", notional_value_dollars = "1.00",
                        yes_bid_dollars = x.Item2, yes_ask_dollars = x.Item3, yes_bid_size_fp = "20.00", yes_ask_size_fp = "5.00",
                        close_time = "2027-04-25T14:00:00Z"
                    })
                }
            });
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        using var http = new HttpClient(handler);
        var importer = new KalshiPlayoffRatingsService(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<KalshiPlayoffRatingsService>.Instance, app.Services, clock);
        Assert.Equal(1, await importer.RefreshAsync("nhl", 2027, default));
        var cbj = await db.TeamRatings.AsNoTracking().SingleAsync(r => r.Sport == "nhl" && r.Source == "kalshi" && r.TeamAbbreviation == "CBJ");
        Assert.Null(cbj.PlayoffProbability); Assert.Equal(clock.Now.UtcDateTime, cbj.FetchedAt);
        var html = await client.GetStringAsync("/game/" + game.Id);
        AssertNoScoringDiagnostics(html);
        using var doc = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(html, CancellationToken.None);
        Assert.Contains("Hype needs reliable playoff odds for Columbus Blue Jackets.", doc.QuerySelector("#detail-hype-status")!.TextContent);
        Assert.Contains("Quality59", doc.QuerySelector("#detail-hype")!.TextContent);
        Assert.Contains("Closeness99", doc.QuerySelector("#detail-hype")!.TextContent);
        Assert.Null(doc.QuerySelector("#detail-data"));
        Assert.DoesNotContain("Kalshi", doc.QuerySelector("#detail-comparison")!.TextContent);
        foreach (var path in new[] { "/", "/nhl?date=2026-09-30", "/api/games?sport=nhl", $"/api/games/{game.Id}/odds" })
            AssertNoScoringDiagnostics(await client.GetStringAsync(path));

        cbjAsk = "0.4000";
        Assert.Equal(2, await importer.RefreshAsync("nhl", 2027, default));
        var text = await BodyText(await client.GetAsync("/game/" + game.Id));
        Assert.DoesNotContain("Hype unavailable", text);
        Assert.DoesNotContain("Hype needs reliable playoff odds", text);
    }

    private static readonly (string Name, string Abbr, int NoPlayoffs)[] NhlTeams =
    [
        ("Anaheim Ducks", "ANA", 65), ("Boston Bruins", "BOS", 49), ("Buffalo Sabres", "BUF", 58), ("Calgary Flames", "CGY", 62),
        ("Carolina Hurricanes", "CAR", 18), ("Chicago Blackhawks", "CHI", 76), ("Colorado Avalanche", "COL", 22), ("Columbus Blue Jackets", "CBJ", 49),
        ("Dallas Stars", "DAL", 24), ("Detroit Red Wings", "DET", 57), ("Edmonton Oilers", "EDM", 19), ("Florida Panthers", "FLA", 21),
        ("Los Angeles Kings", "LA", 33), ("Minnesota Wild", "MIN", 45), ("Montreal Canadiens", "MTL", 54), ("Nashville Predators", "NSH", 52),
        ("New Jersey Devils", "NJ", 25), ("New York Islanders", "NYI", 59), ("New York Rangers", "NYR", 43), ("Ottawa Senators", "OTT", 48),
        ("Philadelphia Flyers", "PHI", 49), ("Pittsburgh Penguins", "PIT", 28), ("San Jose Sharks", "SJ", 82), ("Seattle Kraken", "SEA", 61),
        ("St. Louis Blues", "STL", 50), ("Tampa Bay Lightning", "TB", 28), ("Toronto Maple Leafs", "TOR", 26), ("Utah Mammoth", "UTAH", 56),
        ("Vancouver Canucks", "VAN", 46), ("Vegas Golden Knights", "VGK", 20), ("Washington Capitals", "WSH", 42), ("Winnipeg Jets", "WPG", 34)
    ];

    internal static string PlayoffStatusFixture(string url, DateTime publishedAt, bool invalidBos,
        string? publishedAtLiteral = null, string? metaName = "nailbiter-approved-source-published-at",
        Func<string, string, string, string>? cellOverride = null, string? omitTeamAbbr = null,
        bool omitFromStandingsOnly = false, bool truncateStandings = false,
        Func<string, string>? titleOverride = null, string extraBody = "")
    {
        var isPostseason = url.Contains("postseason", StringComparison.OrdinalIgnoreCase);
        var teams = url.Contains("eastern", StringComparison.OrdinalIgnoreCase) ? NhlTeams.Take(16) :
            url.Contains("western", StringComparison.OrdinalIgnoreCase) ? NhlTeams.Skip(16) : NhlTeams;
        teams = teams.Where(t => t.Abbr != omitTeamAbbr || (omitFromStandingsOnly && isPostseason));
        var rank = 0;
        var rows = string.Join("", teams.Select(t =>
        {
            rank++;
            var noDefault = invalidBos && t.Abbr == "BOS" ? "n/a" : $"{t.NoPlayoffs}%";
            var noPlayoffs = cellOverride?.Invoke(t.Abbr, "No Playoffs", noDefault) ?? noDefault;
            if (isPostseason)
            {
                var roundDefault = invalidBos && t.Abbr == "BOS" ? "n/a" : $"{100 - t.NoPlayoffs}%";
                var roundOne = cellOverride?.Invoke(t.Abbr, "Round 1", roundDefault) ?? roundDefault;
                var cup = cellOverride?.Invoke(t.Abbr, "Stanley Cup", "1%") ?? "1%";
                return $"<tr><td>{rank}</td><td><a>{t.Name}</a></td><td>{roundOne}</td><td>{noPlayoffs}</td><td>{cup}</td></tr>";
            }
            return $"<tr><td>{rank}</td><td><a>{t.Name}</a></td><td>{noPlayoffs}</td></tr>";
        }));
        var header = isPostseason
            ? "<tr><th colspan='5'>Synthetic PlayoffStatus-like offline fixture</th></tr><tr><th>Rank</th><th>Team</th><th>Round 1</th><th>No Playoffs</th><th>Stanley Cup</th></tr>"
            : "<tr><th colspan='3'>Synthetic NHL Wild Card table</th></tr><tr><th>Rank</th><th>Team</th><th>No Playoffs</th></tr>";
        var title = titleOverride?.Invoke(url) ?? (isPostseason
            ? "2027 NHL Postseason Probabilities"
            : "2026-27 NHL Wild Card Standings");
        var meta = metaName is null ? "" : $"<meta name='{metaName}' content='{publishedAtLiteral ?? publishedAt.ToString("O")}' />";
        var completion = isPostseason ? "" : "<tfoot><tr data-nailbiter-supported-complete='nhl-standings'><td colspan='3'>Complete supported NHL standings document</td></tr></tfoot>";
        var html = $"<html><head>{meta}</head><body><h1>{title}</h1>{extraBody}<table>{header}{rows}{completion}</table></body></html>";
        return truncateStandings && !isPostseason ? html.Replace(completion, "").Replace("</table></body></html>", "") : html;
    }

    private static string LegacyPlayoffStatusFixture(string url)
    {
        (string Name, string NoPlayoffs)[] teams = url.Contains("eastern", StringComparison.OrdinalIgnoreCase)
            ? [("Boston Celtics", "X"), ("New York Knicks", "^")]
            : [("Los Angeles Lakers", ">50%"), ("Golden State Warriors", "<1%")];
        if (url.Contains("postseason", StringComparison.OrdinalIgnoreCase))
        {
            (string Name, string Championship)[] postseasonTeams = [("Boston Celtics", "^"), ("New York Knicks", "X"), ("Los Angeles Lakers", ">50%"), ("Golden State Warriors", "<1%")];
            var postseasonRows = string.Join("", postseasonTeams.Select(t => $"<tr><td>{t.Name}</td><td></td><td></td><td></td><td></td><td>{t.Championship}</td></tr>"));
            return $"<html><body><table><tr><th>header</th></tr><tr><th>header</th></tr>{postseasonRows}</table></body></html>";
        }
        var rows = string.Join("", teams.Select((t, i) => $"<tr data-idx='{i}'><td><a>{t.Name}</a></td><td></td><td></td><td></td><td>{t.NoPlayoffs}</td></tr>"));
        return $"<html><body><table>{rows}</table></body></html>";
    }

    private static string KalshiFinalEvidence(string sport, string team, int season, double probability, DateTime observedAt)
    {
        var ticker = $"KXNHLPLAYOFF-{season % 100:D2}";
        var evidence = new PlayoffQuoteEvidence
        {
            Version = 1,
            ValidationPolicy = sport == "nhl" ? "kalshi-qualification-v1" : "kalshi-test-final-v1",
            Provider = PlayoffRatingPolicy.kalshiSource,
            Sport = sport,
            Team = team,
            Season = season,
            EventId = sport == "nhl" ? ticker : $"{sport}-{season}",
            MarketId = sport == "nhl" ? ticker + "-" + (team == "UTAH" ? "UTA" : team) : $"{sport}-{season}-{team}",
            ContractVerified = true,
            State = "final",
            ReasonCode = "accepted",
            ObservedAt = observedAt,
            EvidenceAt = observedAt,
            ResolutionAt = observedAt,
            YesPayout = (decimal)probability,
            NoPayout = 1M - (decimal)probability,
            ResolutionReference = "synthetic-test-final"
        };
        return System.Text.Json.JsonSerializer.Serialize(evidence);
    }

    private static TeamRating ModelRow(string team, double probability, DateTime publishedAt, DateTime fetchedAt)
    {
        var evidence = new PlayoffModelEvidence
        {
            Version = 2,
            EvidenceKind = "model_projection",
            ValidationPolicy = PlayoffRatingPolicy.playoffStatusValidationPolicy,
            Provider = PlayoffRatingPolicy.playoffStatusSource,
            Sport = "nhl",
            Team = team,
            Season = 2027,
            SeasonVerified = true,
            TeamVerified = true,
            State = "estimate",
            ReasonCode = "accepted",
            PublishedAt = publishedAt,
            FetchedAt = fetchedAt,
            AcceptedTeamCount = 32,
            TotalTeamCount = 32
        };
        return new TeamRating
        {
            Sport = "nhl",
            TeamAbbreviation = team,
            Season = 2027,
            SourceSeason = 2027,
            Source = PlayoffRatingPolicy.playoffStatusSource,
            PlayoffProbability = probability,
            FetchedAt = fetchedAt,
            PlayoffEvidenceJson = System.Text.Json.JsonSerializer.Serialize(evidence)
        };
    }

    private sealed class ProFixtureHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request.RequestUri!.ToString()));
    }

    private sealed class AsyncProFixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }

    private sealed class CancelCommitInterceptor(CancellationTokenSource cts) : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        public override InterceptionResult TransactionCommitting(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result) =>
            Enabled ? throw Cancel() : result;
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            Enabled ? ValueTask.FromException<InterceptionResult>(Cancel()) : ValueTask.FromResult(result);
        private OperationCanceledException Cancel()
        {
            cts.Cancel();
            return new OperationCanceledException(cts.Token);
        }
    }

    private sealed class RefreshRaceInterceptor(string connectionString) : DbCommandInterceptor
    {
        private int armed;
        public void Arm() => Volatile.Write(ref armed, 1);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref armed) == 1
                && command.CommandText.Contains("team_ratings", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref armed, 0) == 1)
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var update = connection.CreateCommand();
                update.CommandText = "UPDATE team_ratings SET playoff_probability = NULL, playoff_evidence_json = jsonb_set(playoff_evidence_json::jsonb, '{ReasonCode}', '\"refresh_race_invalidated\"')::text WHERE source = 'playoffstatus' AND team_abbreviation = 'PHI'";
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            return result;
        }
    }

    private WebApplicationFactory<Program> CreateAppWithSettings(Dictionary<string, string?> settings,
        Action<DbContextOptionsBuilder>? configureDb = null, Action<IServiceCollection>? configureServices = null) =>
        entryPoint.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
                services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
                services.AddDbContext<ExcitementDbContext>(options =>
                {
                    options.UseNpgsql(database.GetConnectionString());
                    configureDb?.Invoke(options);
                });
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                configureServices?.Invoke(services);
            });
        });

    [Fact]
    public async Task PlayoffStatus_model_refresh_is_disabled_by_default_then_uses_publication_time_cooldown_and_latest_invalid_state()
    {
        clock.Now = new DateTimeOffset(ProHypeTests.Now);
        var requests = 0;
        var published = ProHypeTests.Now.AddHours(-24);
        var invalidBos = false;
        var omitBosFromStandingsOnly = false;
        var truncateBosFromStandingsOnly = false;
        var fail = false;
        using var handler = new ProFixtureHandler(url =>
        {
            requests++;
            if (fail) return new(HttpStatusCode.ServiceUnavailable);
            return new(HttpStatusCode.OK) { Content = new StringContent(PlayoffStatusFixture(url, published, invalidBos,
                omitTeamAbbr: omitBosFromStandingsOnly || truncateBosFromStandingsOnly ? "BOS" : null,
                omitFromStandingsOnly: omitBosFromStandingsOnly || truncateBosFromStandingsOnly,
                truncateStandings: truncateBosFromStandingsOnly)) };
        });
        using var http = new HttpClient(handler);
        var disabled = new PlayoffStatusService(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayoffStatusService>.Instance,
            app.Services, clock, Microsoft.Extensions.Options.Options.Create(new PlayoffRatingsOptions()), new PlayoffStatusNhlRefreshGate(clock));
        Assert.Equal(0, await disabled.RefreshNhlRatingsForSeasonAsync(2027, default));
        Assert.Equal(0, requests);
        var offSwitch = new PlayoffStatusService(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayoffStatusService>.Instance,
            app.Services, clock, Microsoft.Extensions.Options.Options.Create(new PlayoffRatingsOptions { NhlModelMode = "off", NhlModelRefreshEnabled = true, ApprovedSeasons = [2027] }), new PlayoffStatusNhlRefreshGate(clock));
        Assert.Equal(0, await offSwitch.RefreshNhlRatingsForSeasonAsync(2027, default));
        Assert.Equal(0, requests);

        var options = new PlayoffRatingsOptions { NhlModelMode = "fallback", NhlModelRefreshEnabled = true, ApprovedSeasons = [2027] };
        var service = new PlayoffStatusService(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayoffStatusService>.Instance,
            app.Services, clock, Microsoft.Extensions.Options.Options.Create(options), new PlayoffStatusNhlRefreshGate(clock));
        Assert.Equal(32, await service.RefreshNhlRatingsForSeasonAsync(2027, default));
        Assert.Equal(3, requests);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var bos = await db.TeamRatings.AsNoTracking().SingleAsync(r => r.Sport == "nhl" && r.Source == "playoffstatus" && r.TeamAbbreviation == "BOS");
        Assert.Equal(.51, bos.PlayoffProbability!.Value, 6);
        Assert.Equal(2027, bos.SourceSeason);
        Assert.False(bos.PlayoffProbabilityIsFinal);
        var evidence = PlayoffModelEvidence.Parse(bos.PlayoffEvidenceJson)!;
        Assert.Equal(published, evidence.PublishedAt);
        Assert.Equal(ProHypeTests.Now, evidence.FetchedAt);
        Assert.Equal("model_projection", evidence.EvidenceKind);

        Assert.Equal(0, await service.RefreshNhlRatingsForSeasonAsync(2027, default));
        Assert.Equal(3, requests);

        fail = true;
        clock.Now = clock.Now.AddHours(6);
        Assert.Equal(0, await service.RefreshNhlRatingsForSeasonAsync(2027, default));
        db.ChangeTracker.Clear();
        Assert.Equal(ProHypeTests.Now, (await db.TeamRatings.AsNoTracking().SingleAsync(r => r.Id == bos.Id)).FetchedAt);

        fail = false;
        published = published.AddHours(-1);
        clock.Now = clock.Now.AddHours(6);
        Assert.Equal(0, await service.RefreshNhlRatingsForSeasonAsync(2027, default));
        db.ChangeTracker.Clear();
        Assert.Equal(.51, (await db.TeamRatings.AsNoTracking().SingleAsync(r => r.Id == bos.Id)).PlayoffProbability!.Value, 6);

        published = ProHypeTests.Now.AddMinutes(-30);
        truncateBosFromStandingsOnly = true;
        clock.Now = clock.Now.AddHours(6);
        Assert.Equal(0, await service.RefreshNhlRatingsForSeasonAsync(2027, default));
        db.ChangeTracker.Clear();
        bos = await db.TeamRatings.AsNoTracking().SingleAsync(r => r.Id == bos.Id);
        Assert.Equal(.51, bos.PlayoffProbability!.Value, 6);
        Assert.Equal(evidence.PublishedAt, PlayoffModelEvidence.Parse(bos.PlayoffEvidenceJson)!.PublishedAt);
        truncateBosFromStandingsOnly = false;

        published = ProHypeTests.Now.AddMinutes(-20);
        omitBosFromStandingsOnly = true;
        clock.Now = clock.Now.AddHours(6);
        Assert.Equal(31, await service.RefreshNhlRatingsForSeasonAsync(2027, default));
        db.ChangeTracker.Clear();
        bos = await db.TeamRatings.AsNoTracking().SingleAsync(r => r.Id == bos.Id);
        Assert.Null(bos.PlayoffProbability);
        var missingEvidence = PlayoffModelEvidence.Parse(bos.PlayoffEvidenceJson)!;
        Assert.Equal("missing_standings_probability", missingEvidence.ReasonCode);
        Assert.Equal(published, missingEvidence.PublishedAt);
        omitBosFromStandingsOnly = false;

        published = ProHypeTests.Now.AddMinutes(-5);
        invalidBos = true;
        clock.Now = clock.Now.AddHours(6);
        Assert.Equal(31, await service.RefreshNhlRatingsForSeasonAsync(2027, default));
        db.ChangeTracker.Clear();
        bos = await db.TeamRatings.AsNoTracking().SingleAsync(r => r.Id == bos.Id);
        Assert.Null(bos.PlayoffProbability);
        Assert.Equal("malformed_probability", PlayoffModelEvidence.Parse(bos.PlayoffEvidenceJson)!.ReasonCode);
    }

    [Theory]
    [InlineData("No Playoffs", "NaN", "malformed_probability")]
    [InlineData("No Playoffs", ">50%", "malformed_probability")]
    [InlineData("No Playoffs", "^", "malformed_probability")]
    [InlineData("No Playoffs", "101%", "malformed_probability")]
    [InlineData("Round 1", "", "malformed_round1_probability")]
    public async Task PlayoffStatus_model_refresh_rejects_unsupported_scalars_and_requires_round_one_corroboration(
        string column, string value, string expectedReason)
    {
        clock.Now = new DateTimeOffset(ProHypeTests.Now);
        using var handler = new ProFixtureHandler(url => new(HttpStatusCode.OK)
        {
            Content = new StringContent(PlayoffStatusFixture(url, ProHypeTests.Now.AddHours(-1), false,
                cellOverride: (team, col, current) => team == "BOS" && col == column ? value : current))
        });
        using var http = new HttpClient(handler);
        var options = new PlayoffRatingsOptions { NhlModelMode = "fallback", NhlModelRefreshEnabled = true, ApprovedSeasons = [2027] };
        var service = new PlayoffStatusService(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayoffStatusService>.Instance,
            app.Services, clock, Microsoft.Extensions.Options.Options.Create(options), new PlayoffStatusNhlRefreshGate(clock));

        Assert.Equal(31, await service.RefreshNhlRatingsForSeasonAsync(2027, default));
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var bos = await db.TeamRatings.AsNoTracking().SingleAsync(r => r.Sport == "nhl" && r.Source == "playoffstatus" && r.TeamAbbreviation == "BOS");
        Assert.Null(bos.PlayoffProbability);
        Assert.Equal(expectedReason, PlayoffModelEvidence.Parse(bos.PlayoffEvidenceJson)!.ReasonCode);
    }

    [Fact]
    public async Task Legacy_non_NHL_PlayoffStatus_refresh_preserves_marker_probability_semantics()
    {
        using var handler = new ProFixtureHandler(url => new(HttpStatusCode.OK)
        {
            Content = new StringContent(LegacyPlayoffStatusFixture(url))
        });
        using var http = new HttpClient(handler);
        var service = new PlayoffStatusService(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayoffStatusService>.Instance,
            app.Services, clock, Microsoft.Extensions.Options.Options.Create(new PlayoffRatingsOptions()), new PlayoffStatusNhlRefreshGate(clock));

        Assert.Equal(4, await service.RefreshNbaRatingsAsync(default));
        var season = DateTime.UtcNow.Month >= 10 ? DateTime.UtcNow.Year + 1 : DateTime.UtcNow.Year;
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var rows = await db.TeamRatings.AsNoTracking().Where(r => r.Sport == "nba" && r.Source == "playoffstatus" && r.Season == season).ToDictionaryAsync(r => r.TeamAbbreviation);
        Assert.Equal(1.0, rows["BOS"].PlayoffProbability!.Value, 6);
        Assert.Equal(0.0, rows["NY"].PlayoffProbability!.Value, 6);
        Assert.Equal(0.495, rows["LAL"].PlayoffProbability!.Value, 6);
        Assert.Equal(0.995, rows["GS"].PlayoffProbability!.Value, 6);
        Assert.Equal(1.0, rows["BOS"].ChampionshipProbability!.Value, 6);
        Assert.Equal(0.0, rows["NY"].ChampionshipProbability!.Value, 6);
    }

    [Fact]
    public async Task PlayoffStatus_model_refresh_requires_offset_publication_clock_and_singleflight()
    {
        clock.Now = new DateTimeOffset(ProHypeTests.Now);
        var requests = 0;
        using var timezoneFreeHandler = new ProFixtureHandler(url =>
        {
            requests++;
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(PlayoffStatusFixture(url, ProHypeTests.Now.AddHours(-1), false,
                    publishedAtLiteral: ProHypeTests.Now.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss")))
            };
        });
        using var badHttp = new HttpClient(timezoneFreeHandler);
        var options = new PlayoffRatingsOptions { NhlModelMode = "fallback", NhlModelRefreshEnabled = true, ApprovedSeasons = [2027] };
        var service = new PlayoffStatusService(badHttp, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayoffStatusService>.Instance,
            app.Services, clock, Microsoft.Extensions.Options.Options.Create(options), new PlayoffStatusNhlRefreshGate(clock));
        Assert.Equal(0, await service.RefreshNhlRatingsForSeasonAsync(2027, default));
        Assert.Equal(3, requests);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            Assert.Empty(await db.TeamRatings.AsNoTracking().Where(r => r.Source == "playoffstatus").ToListAsync());
        }

        using var timeOnlyClockHandler = new ProFixtureHandler(url => new(HttpStatusCode.OK)
        {
            Content = new StringContent(PlayoffStatusFixture(url, ProHypeTests.Now.AddHours(-1), false,
                publishedAtLiteral: "03:00Z"))
        });
        service = new PlayoffStatusService(new HttpClient(timeOnlyClockHandler), Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayoffStatusService>.Instance,
            app.Services, clock, Microsoft.Extensions.Options.Options.Create(options), new PlayoffStatusNhlRefreshGate(clock));
        Assert.Equal(0, await service.RefreshNhlRatingsForSeasonAsync(2027, default));

        using var partialClockHandler = new ProFixtureHandler(url => new(HttpStatusCode.OK)
        {
            Content = new StringContent(PlayoffStatusFixture(url, ProHypeTests.Now.AddHours(-1), false,
                metaName: url.Contains("postseason", StringComparison.OrdinalIgnoreCase) ? null : "nailbiter-approved-source-published-at"))
        });
        service = new PlayoffStatusService(new HttpClient(partialClockHandler), Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayoffStatusService>.Instance,
            app.Services, clock, Microsoft.Extensions.Options.Options.Create(options), new PlayoffStatusNhlRefreshGate(clock));
        Assert.Equal(0, await service.RefreshNhlRatingsForSeasonAsync(2027, default));

        using var conflictingClockHandler = new ProFixtureHandler(url => new(HttpStatusCode.OK)
        {
            Content = new StringContent(PlayoffStatusFixture(url, ProHypeTests.Now.AddHours(-1), false,
                publishedAtLiteral: (url.Contains("western", StringComparison.OrdinalIgnoreCase) ? ProHypeTests.Now.AddHours(-2) : ProHypeTests.Now.AddHours(-1)).ToString("O")))
        });
        service = new PlayoffStatusService(new HttpClient(conflictingClockHandler), Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayoffStatusService>.Instance,
            app.Services, clock, Microsoft.Extensions.Options.Options.Create(options), new PlayoffStatusNhlRefreshGate(clock));
        Assert.Equal(0, await service.RefreshNhlRatingsForSeasonAsync(2027, default));

        using var futureClockHandler = new ProFixtureHandler(url => new(HttpStatusCode.OK)
        {
            Content = new StringContent(PlayoffStatusFixture(url, ProHypeTests.Now.AddMinutes(1), false))
        });
        service = new PlayoffStatusService(new HttpClient(futureClockHandler), Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayoffStatusService>.Instance,
            app.Services, clock, Microsoft.Extensions.Options.Options.Create(options), new PlayoffStatusNhlRefreshGate(clock));
        Assert.Equal(0, await service.RefreshNhlRatingsForSeasonAsync(2027, default));

        using var wrongHeadingHandler = new ProFixtureHandler(url => new(HttpStatusCode.OK)
        {
            Content = new StringContent(PlayoffStatusFixture(url, ProHypeTests.Now.AddHours(-1), false,
                titleOverride: u => u.Contains("postseason", StringComparison.OrdinalIgnoreCase)
                    ? "2026 NHL Postseason Probabilities"
                    : "2025-26 NHL Wild Card Standings",
                extraBody: "<nav>2026-27 NHL Wild Card Standings · 2027 NHL Postseason Probabilities</nav>"))
        });
        service = new PlayoffStatusService(new HttpClient(wrongHeadingHandler), Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayoffStatusService>.Instance,
            app.Services, clock, Microsoft.Extensions.Options.Options.Create(options), new PlayoffStatusNhlRefreshGate(clock));
        Assert.Equal(0, await service.RefreshNhlRatingsForSeasonAsync(2027, default));

        using var mixedHeadingHandler = new ProFixtureHandler(url =>
        {
            var staleAllowedHeading = url.Contains("postseason", StringComparison.OrdinalIgnoreCase)
                ? "<h2>2026 NHL Postseason Probabilities</h2>"
                : "<h2>2025-26 NHL Wild Card Standings</h2>";
            var body = PlayoffStatusFixture(url, ProHypeTests.Now.AddHours(-1), false)
                .Replace("<body>", "<body>" + staleAllowedHeading);
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        service = new PlayoffStatusService(new HttpClient(mixedHeadingHandler), Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayoffStatusService>.Instance,
            app.Services, clock, Microsoft.Extensions.Options.Options.Create(options), new PlayoffStatusNhlRefreshGate(clock));
        Assert.Equal(0, await service.RefreshNhlRatingsForSeasonAsync(2027, default));

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            Assert.Empty(await db.TeamRatings.AsNoTracking().Where(r => r.Source == "playoffstatus").ToListAsync());
        }

        clock.Now = clock.Now.AddHours(6);
        using var truncatedHandler = new ProFixtureHandler(url =>
        {
            requests++;
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(PlayoffStatusFixture(url, ProHypeTests.Now.AddHours(-1), false, omitTeamAbbr: "BOS", truncateStandings: true))
            };
        });
        using var truncatedHttp = new HttpClient(truncatedHandler);
        service = new PlayoffStatusService(truncatedHttp, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayoffStatusService>.Instance,
            app.Services, clock, Microsoft.Extensions.Options.Options.Create(options), new PlayoffStatusNhlRefreshGate(clock));
        Assert.Equal(0, await service.RefreshNhlRatingsForSeasonAsync(2027, default));
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            Assert.Empty(await db.TeamRatings.AsNoTracking().Where(r => r.Source == "playoffstatus").ToListAsync());
        }

        requests = 0;
        using var validHandler = new ProFixtureHandler(url =>
        {
            requests++;
            return new(HttpStatusCode.OK) { Content = new StringContent(PlayoffStatusFixture(url, ProHypeTests.Now.AddHours(-1), false)) };
        });
        using var validHttp = new HttpClient(validHandler);
        service = new PlayoffStatusService(validHttp, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayoffStatusService>.Instance,
            app.Services, clock, Microsoft.Extensions.Options.Options.Create(options), new PlayoffStatusNhlRefreshGate(clock));
        var results = await Task.WhenAll(service.RefreshNhlRatingsForSeasonAsync(2027, default), service.RefreshNhlRatingsForSeasonAsync(2027, default));
        Assert.Equal(32, results.Sum());
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task PlayoffStatus_model_refresh_cancellation_retains_previous_rows_without_partial_commit()
    {
        clock.Now = new DateTimeOffset(ProHypeTests.Now);
        var options = new PlayoffRatingsOptions { NhlModelMode = "fallback", NhlModelRefreshEnabled = true, ApprovedSeasons = [2027] };
        var initialPublished = ProHypeTests.Now.AddHours(-24);
        using (var handler = new ProFixtureHandler(url => new(HttpStatusCode.OK)
        {
            Content = new StringContent(PlayoffStatusFixture(url, initialPublished, false))
        }))
        using (var http = new HttpClient(handler))
        {
            var service = new PlayoffStatusService(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayoffStatusService>.Instance,
                app.Services, clock, Microsoft.Extensions.Options.Options.Create(options), new PlayoffStatusNhlRefreshGate(clock));
            Assert.Equal(32, await service.RefreshNhlRatingsForSeasonAsync(2027, default));
        }

        using var cts = new CancellationTokenSource();
        var requests = 0;
        using var cancellingHandler = new AsyncProFixtureHandler((request, _) =>
        {
            var count = Interlocked.Increment(ref requests);
            if (count == 3)
            {
                cts.Cancel();
                return Task.FromCanceled<HttpResponseMessage>(cts.Token);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(PlayoffStatusFixture(request.RequestUri!.ToString(), ProHypeTests.Now.AddMinutes(-10), true))
            });
        });
        using var cancellingHttp = new HttpClient(cancellingHandler);
        clock.Now = clock.Now.AddHours(6);
        var cancellingService = new PlayoffStatusService(cancellingHttp, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayoffStatusService>.Instance,
            app.Services, clock, Microsoft.Extensions.Options.Options.Create(options), new PlayoffStatusNhlRefreshGate(clock));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellingService.RefreshNhlRatingsForSeasonAsync(2027, cts.Token));

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var rows = await db.TeamRatings.AsNoTracking().Where(r => r.Sport == "nhl" && r.Source == "playoffstatus").ToListAsync();
        Assert.Equal(32, rows.Count);
        var bos = rows.Single(r => r.TeamAbbreviation == "BOS");
        Assert.Equal(.51, bos.PlayoffProbability!.Value, 6);
        Assert.Equal(initialPublished, PlayoffModelEvidence.Parse(bos.PlayoffEvidenceJson)!.PublishedAt);
    }

    [Fact]
    public async Task PlayoffStatus_model_refresh_rolls_back_real_database_failure_and_commit_cancellation()
    {
        clock.Now = new DateTimeOffset(ProHypeTests.Now);
        var options = new PlayoffRatingsOptions { NhlModelMode = "fallback", NhlModelRefreshEnabled = true, ApprovedSeasons = [2027] };
        var initialPublished = ProHypeTests.Now.AddHours(-24);
        using (var handler = new ProFixtureHandler(url => new(HttpStatusCode.OK)
        {
            Content = new StringContent(PlayoffStatusFixture(url, initialPublished, false))
        }))
        using (var http = new HttpClient(handler))
        {
            var service = new PlayoffStatusService(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayoffStatusService>.Instance,
                app.Services, clock, Microsoft.Extensions.Options.Options.Create(options), new PlayoffStatusNhlRefreshGate(clock));
            Assert.Equal(32, await service.RefreshNhlRatingsForSeasonAsync(2027, default));
        }

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE OR REPLACE FUNCTION fail_late_playoffstatus_update() RETURNS trigger AS $$
            BEGIN
                IF NEW.source = 'playoffstatus' AND NEW.team_abbreviation = 'WPG' THEN
                    RAISE EXCEPTION 'late playoffstatus failure';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            DROP TRIGGER IF EXISTS fail_late_playoffstatus_update_trigger ON team_ratings;
            CREATE TRIGGER fail_late_playoffstatus_update_trigger BEFORE UPDATE ON team_ratings FOR EACH ROW EXECUTE FUNCTION fail_late_playoffstatus_update();
            """);
        try
        {
            using var lateFailureHandler = new ProFixtureHandler(url => new(HttpStatusCode.OK)
            {
                Content = new StringContent(PlayoffStatusFixture(url, ProHypeTests.Now.AddMinutes(-10), true))
            });
            using var lateFailureHttp = new HttpClient(lateFailureHandler);
            clock.Now = clock.Now.AddHours(6);
            var lateFailure = new PlayoffStatusService(lateFailureHttp, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayoffStatusService>.Instance,
                app.Services, clock, Microsoft.Extensions.Options.Options.Create(options), new PlayoffStatusNhlRefreshGate(clock));
            Assert.Equal(0, await lateFailure.RefreshNhlRatingsForSeasonAsync(2027, default));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS fail_late_playoffstatus_update_trigger ON team_ratings; DROP FUNCTION IF EXISTS fail_late_playoffstatus_update();");
        }
        db.ChangeTracker.Clear();
        var bos = await db.TeamRatings.AsNoTracking().SingleAsync(r => r.Source == "playoffstatus" && r.TeamAbbreviation == "BOS");
        Assert.Equal(.51, bos.PlayoffProbability!.Value, 6);
        Assert.Equal(initialPublished, PlayoffModelEvidence.Parse(bos.PlayoffEvidenceJson)!.PublishedAt);

        using var cts = new CancellationTokenSource();
        var cancelCommit = new CancelCommitInterceptor(cts);
        await using var cancelApp = CreateAppWithSettings(new Dictionary<string, string?>
        {
            ["PlayoffRatings:NhlModelMode"] = "fallback",
            ["PlayoffRatings:NhlModelRefreshEnabled"] = "true",
            ["PlayoffRatings:ApprovedSeasons:0"] = "2027"
        }, builder => builder.AddInterceptors(cancelCommit));
        using var commitCancelHandler = new ProFixtureHandler(url => new(HttpStatusCode.OK)
        {
            Content = new StringContent(PlayoffStatusFixture(url, ProHypeTests.Now.AddMinutes(-5), true))
        });
        using var commitCancelHttp = new HttpClient(commitCancelHandler);
        cancelCommit.Enabled = true;
        clock.Now = clock.Now.AddHours(6);
        var commitCancel = new PlayoffStatusService(commitCancelHttp, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayoffStatusService>.Instance,
            cancelApp.Services, clock, Microsoft.Extensions.Options.Options.Create(options), new PlayoffStatusNhlRefreshGate(clock));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commitCancel.RefreshNhlRatingsForSeasonAsync(2027, cts.Token));

        db.ChangeTracker.Clear();
        bos = await db.TeamRatings.AsNoTracking().SingleAsync(r => r.Source == "playoffstatus" && r.TeamAbbreviation == "BOS");
        Assert.Equal(.51, bos.PlayoffProbability!.Value, 6);
        Assert.Equal(initialPublished, PlayoffModelEvidence.Parse(bos.PlayoffEvidenceJson)!.PublishedAt);
    }

    [Fact]
    public async Task PlayoffStatus_singleflight_is_shared_between_real_background_and_manual_refresh_while_Kalshi_continues()
    {
        clock.Now = new DateTimeOffset(ProHypeTests.Now.AddDays(1));
        var firstRequestEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var blockedHandler = new AsyncProFixtureHandler(async (request, token) =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                firstRequestEntered.SetResult();
                await releaseFirstRequest.Task.WaitAsync(token);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(PlayoffStatusFixture(request.RequestUri!.ToString(), ProHypeTests.Now.AddHours(-1), false))
            };
        });
        await using var enabledApp = CreateAppWithSettings(new Dictionary<string, string?>
        {
            ["PlayoffRatings:NhlModelMode"] = "fallback",
            ["PlayoffRatings:NhlModelRefreshEnabled"] = "true",
            ["PlayoffRatings:ApprovedSeasons:0"] = "2027"
        }, configureServices: services =>
        {
            services.RemoveAll<PlayoffStatusService>();
            services.AddScoped(sp => new PlayoffStatusService(new HttpClient(blockedHandler, disposeHandler: false),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayoffStatusService>.Instance,
                sp,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PlayoffRatingsOptions>>(),
                sp.GetRequiredService<PlayoffStatusNhlRefreshGate>()));
        });
        var background = ActivatorUtilities.CreateInstance<PlayoffStatusNhlRefreshBackgroundService>(enabledApp.Services);
        using var backgroundCts = new CancellationTokenSource();
        await background.StartAsync(backgroundCts.Token);
        await firstRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var manualScope = enabledApp.Services.CreateScope();
        var manual = manualScope.ServiceProvider.GetRequiredService<PlayoffStatusService>();
        Assert.Equal(0, await manual.RefreshNhlRatingsForSeasonAsync(2027, default));

        using var kalshiHandler = new ProFixtureHandler(url =>
        {
            var body = url.Contains("/historical/", StringComparison.OrdinalIgnoreCase) ? "{\"markets\":[],\"cursor\":\"\"}" : System.Text.Json.JsonSerializer.Serialize(new
            {
                @event = new
                {
                    event_ticker = "KXNHLPLAYOFF-27",
                    series_ticker = "KXNHLPLAYOFF",
                    sub_title = "2026-27",
                    markets = new[] { ("PHI", "0.5000", "0.5200"), ("PIT", "0.7000", "0.7400") }.Select(x => new
                    {
                        ticker = "KXNHLPLAYOFF-27-" + x.Item1,
                        event_ticker = "KXNHLPLAYOFF-27",
                        status = "active",
                        result = "",
                        market_type = "binary",
                        notional_value_dollars = "1.00",
                        yes_bid_dollars = x.Item2,
                        yes_ask_dollars = x.Item3,
                        yes_bid_size_fp = "20.00",
                        yes_ask_size_fp = "5.00",
                        close_time = "2027-04-25T14:00:00Z"
                    })
                }
            });
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        using var kalshiHttp = new HttpClient(kalshiHandler);
        var kalshi = new KalshiPlayoffRatingsService(kalshiHttp, Microsoft.Extensions.Logging.Abstractions.NullLogger<KalshiPlayoffRatingsService>.Instance, enabledApp.Services, clock);
        Assert.Equal(2, await kalshi.RefreshAsync("nhl", 2027, default));
        Assert.False(background.ExecuteTask?.IsCompleted ?? true);

        releaseFirstRequest.SetResult();
        for (var i = 0; i < 50; i++)
        {
            using var scope = enabledApp.Services.CreateScope();
            var count = await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().TeamRatings.AsNoTracking().CountAsync(r => r.Source == "playoffstatus");
            if (count == 32) break;
            await Task.Delay(50);
        }
        Assert.Equal(3, requests);
        await background.StopAsync(CancellationToken.None);

        clock.Now = clock.Now.Add(PlayoffRatingPolicy.modelRefreshCooldown).AddTicks(-1);
        Assert.Equal(0, await manual.RefreshNhlRatingsForSeasonAsync(2027, default));
        Assert.Equal(3, requests);
        clock.Now = clock.Now.AddTicks(1);
        Assert.Equal(32, await manual.RefreshNhlRatingsForSeasonAsync(2027, default));
        Assert.Equal(6, requests);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    [InlineData(8, true)]
    [InlineData(9, true)]
    [InlineData(4, false)]
    public async Task Kalshi_qualification_survives_PostgreSQL_precision_loss_in_fresh_scoring_and_pages(int remainder, bool hasCompleteScoringInputs)
    {
        var bucket = new DateTime(2026, 10, 3, 19, 47, 0, DateTimeKind.Utc).AddTicks(8295030);
        var observed = bucket.AddTicks(remainder);
        clock.Now = new DateTimeOffset(observed);
        const string id = "qualification-precision";
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var game = ProHypeTests.Game();
            game.Id = id;
            game.HomeTeam = "Vancouver Canucks"; game.HomeTeamAbbreviation = "VAN";
            game.AwayTeam = "Calgary Flames"; game.AwayTeamAbbreviation = "CGY";
            game.ScheduledStart = bucket.AddHours(4);
            var evidence = ProHypeTests.Evidence(game);
            evidence.ObservedAt = bucket;
            if (!hasCompleteScoringInputs)
                evidence.HomeProgress.SeedingUnresolved = evidence.AwayProgress.SeedingUnresolved = null;
            game.ProEvidenceJson = System.Text.Json.JsonSerializer.Serialize(evidence);
            var odds = ProHypeTests.Odds(game); odds.FetchedAt = bucket;
            db.Games.Add(game); db.PregameOdds.Add(odds);
            if (hasCompleteScoringInputs)
            {
                var ranks = ProHypeTests.Ranks(game);
                foreach (var rank in ranks) rank.FetchedAt = bucket;
                db.TeamRatings.AddRange(ranks);
            }
            await db.SaveChangesAsync();
        }

        var requests = 0;
        using var handler = new ProFixtureHandler(url =>
        {
            requests++;
            var body = url.Contains("/historical/", StringComparison.OrdinalIgnoreCase) ? "{\"markets\":[],\"cursor\":\"\"}" : System.Text.Json.JsonSerializer.Serialize(new
            {
                @event = new
                {
                    event_ticker = "KXNHLPLAYOFF-27", series_ticker = "KXNHLPLAYOFF", sub_title = "2026-27",
                    markets = new[] { ("CGY", "0.1400", "0.1600"), ("VAN", "0.0800", "0.1000") }.Select(x => new
                    {
                        ticker = "KXNHLPLAYOFF-27-" + x.Item1, event_ticker = "KXNHLPLAYOFF-27",
                        status = "active", result = "", market_type = "binary", notional_value_dollars = "1.00",
                        yes_bid_dollars = x.Item2, yes_ask_dollars = x.Item3,
                        yes_bid_size_fp = "20.00", yes_ask_size_fp = "5.00", close_time = "2027-04-25T14:00:00Z"
                    })
                }
            });
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        using var http = new HttpClient(handler);
        using (var importScope = app.Services.CreateScope())
        {
            var importer = new KalshiPlayoffRatingsService(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<KalshiPlayoffRatingsService>.Instance,
                importScope.ServiceProvider, clock);
            Assert.Equal(2, await importer.RefreshAsync("nhl", 2027, default));
        }

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var rows = await db.TeamRatings.AsNoTracking().Where(r => r.Sport == "nhl" && r.Source == "kalshi" && (r.TeamAbbreviation == "VAN" || r.TeamAbbreviation == "CGY")).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, row =>
            {
                Assert.Equal(bucket, row.FetchedAt);
                Assert.Equal(observed, PlayoffQuoteEvidence.Parse(row.PlayoffEvidenceJson)!.ObservedAt);
                Assert.Equal(observed, PlayoffQuoteEvidence.Parse(row.PlayoffEvidenceJson)!.EvidenceAt);
            });
            var game = await db.Games.SingleAsync(g => g.Id == id);
            var data = scope.ServiceProvider.GetRequiredService<GameDataService>();
            Assert.Equal(.09, (await data.GetTeamRankInfoAsync("nhl", "VAN", 2027, 2, default)).Value.PlayoffOdds);
            Assert.Equal(.15, (await data.GetTeamRankInfoAsync("nhl", "CGY", 2027, 2, default)).Value.PlayoffOdds);
            var hype = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
            var components = await hype.GetHypeComponentsAsync(game, null);
            Assert.Equal(.09, components.HomePlayoffOdds); Assert.Equal(.15, components.AwayPlayoffOdds);
            Assert.True(components.StakesAvailable);
            Assert.Equal(hasCompleteScoringInputs, components.QualityAvailable);
            Assert.True(components.ClosenessAvailable);
            Assert.Equal(hasCompleteScoringInputs, components.IsComplete);
            var score = await hype.CalculateHypeScoreAsync(game, null);
            Assert.Equal(hasCompleteScoringInputs, score.HasValue);
            if (hasCompleteScoringInputs)
            {
                Assert.Equal(3.686743952474407, components.StakesScore, 12);
                Assert.Equal(96.849798, components.QualityScore, 5);
                Assert.Equal(100, components.ClosenessScore);
                Assert.Equal(65, Math.Round(score!.Value));
            }
            Assert.Contains("qualification-only", components.StakesExplanation, StringComparison.OrdinalIgnoreCase);
        }

        var beforePages = requests;
        foreach (var path in new[] { "/", "/nhl?date=2026-10-03", "/game/" + id })
        {
            var html = await client.GetStringAsync(path);
            AssertNoScoringDiagnostics(html);
            using var doc = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(html, CancellationToken.None);
            var text = path.StartsWith("/game/", StringComparison.Ordinal) ? doc.Body!.TextContent : doc.QuerySelector($"a[href='/game/{id}']")!.TextContent;
            AssertContainsPercent(9, text); AssertContainsPercent(15, text);
            Assert.DoesNotContain("Hype needs reliable playoff odds", text);
            Assert.Equal(!hasCompleteScoringInputs, text.Contains("Hype unavailable", StringComparison.Ordinal));
            if (!hasCompleteScoringInputs && path.StartsWith("/game/", StringComparison.Ordinal))
            {
                Assert.DoesNotContain("Stakes need verified competition and playoff context.", text);
                Assert.Contains("Current team-strength rankings are unavailable.", text);
            }
        }
        Assert.Equal(beforePages, requests);
    }

    [Fact]
    public async Task PlayoffStatus_refresh_persists_then_pages_and_hype_use_model_without_source_UI_or_page_HTTP()
    {
        clock.Now = new DateTimeOffset(ProHypeTests.Now.AddTicks(4));
        await using var enabledApp = CreateAppWithSettings(new Dictionary<string, string?>
        {
            ["PlayoffRatings:NhlModelMode"] = "fallback",
            ["PlayoffRatings:NhlModelRefreshEnabled"] = "true",
            ["PlayoffRatings:ApprovedSeasons:0"] = "2027"
        });
        using var enabledClient = enabledApp.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using (var scope = enabledApp.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var game = ProHypeTests.Game();
            game.Id = "nhl-refresh-to-pages";
            game.ProEvidenceJson = System.Text.Json.JsonSerializer.Serialize(ProHypeTests.Evidence(game));
            db.Games.Add(game);
            db.PregameOdds.Add(ProHypeTests.Odds(game));
            db.TeamRatings.AddRange(ProHypeTests.Ranks(game));
            await db.SaveChangesAsync();
        }

        var requests = 0;
        using var handler = new ProFixtureHandler(url =>
        {
            requests++;
            return new(HttpStatusCode.OK) { Content = new StringContent(PlayoffStatusFixture(url, ProHypeTests.Now.AddHours(-24), false)) };
        });
        using var http = new HttpClient(handler);
        var options = new PlayoffRatingsOptions { NhlModelMode = "fallback", NhlModelRefreshEnabled = true, ApprovedSeasons = [2027] };
        var service = new PlayoffStatusService(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayoffStatusService>.Instance,
            enabledApp.Services, clock, Microsoft.Extensions.Options.Options.Create(options), new PlayoffStatusNhlRefreshGate(clock));
        Assert.Equal(32, await service.RefreshNhlRatingsForSeasonAsync(2027, default));
        Assert.Equal(3, requests);

        using (var scope = enabledApp.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var models = await db.TeamRatings.AsNoTracking().Where(r => r.Source == "playoffstatus").ToListAsync();
            Assert.All(models, row =>
            {
                Assert.Equal(ProHypeTests.Now, row.FetchedAt);
                Assert.Equal(ProHypeTests.Now.AddTicks(4), PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)!.FetchedAt);
            });
            var game = await db.Games.SingleAsync(g => g.Id == "nhl-refresh-to-pages");
            var rank = await scope.ServiceProvider.GetRequiredService<GameDataService>().GetTeamRankInfoAsync("nhl", game.HomeTeamAbbreviation, 2027, 2, CancellationToken.None);
            Assert.Equal(.51, rank.Value.PlayoffOdds!.Value, 6);
            var hype = await scope.ServiceProvider.GetRequiredService<HypeScoreService>().GetHypeComponentsAsync(game, null);
            Assert.Equal(.51, hype.HomePlayoffOdds.GetValueOrDefault(), 6);
            Assert.Equal(.72, hype.AwayPlayoffOdds.GetValueOrDefault(), 6);
            Assert.True(hype.StakesAvailable, hype.StakesExplanation);
        }

        var beforePages = requests;
        foreach (var path in new[] { "/", "/nhl?date=2026-09-30", "/game/nhl-refresh-to-pages" })
        {
            var html = await enabledClient.GetStringAsync(path);
            AssertNoScoringDiagnostics(html);
            Assert.DoesNotContain("Polymarket", html);
            Assert.DoesNotContain("PlayoffStatus", html);
            Assert.DoesNotContain("playoffstatus", html);
            Assert.DoesNotContain("model", html, StringComparison.OrdinalIgnoreCase);
            using var doc = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(html, CancellationToken.None);
            var text = path.StartsWith("/game/", StringComparison.Ordinal)
                ? doc.Body!.TextContent
                : doc.QuerySelector("a[href='/game/nhl-refresh-to-pages']")!.TextContent;
            Assert.DoesNotContain("Kalshi", text);
            AssertContainsPercent(51, text);
            AssertContainsPercent(72, text);
        }
        Assert.Equal(beforePages, requests);
    }

    [Fact]
    public async Task Real_context_and_rank_importers_replace_unusable_observations_but_not_failed_requests()
    {
        clock.Now = new DateTimeOffset(ProHypeTests.Now);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var game = ProHypeTests.Game(); db.Games.Add(game); await db.SaveChangesAsync();
        var fail = false; var unusable = false;
        using var handler = new ProFixtureHandler(url =>
        {
            if (fail) return new(System.Net.HttpStatusCode.ServiceUnavailable);
            string body;
            if (unusable) body = "{}";
            else if (url.Contains("/scoreboard")) body = ProHypeTests.Fixture("nhl-upcoming-board").GetRawText();
            else if (url.Contains("/schedule")) body = ProHypeTests.Fixture("nhl-schedule").GetRawText();
            else body = "{\"sports\":[{\"leagues\":[{\"season\":{\"year\":2027},\"teams\":[{\"team\":{\"id\":\"15\",\"abbreviation\":\"PHI\"}}]}]}]}";
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        using var http = new HttpClient(handler);
        var collector = new ProCompetitionEvidenceService(http, db, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<ProCompetitionEvidenceService>.Instance);
        await collector.RefreshAsync(default);
        var initial = game.ProEvidenceJson;
        Assert.Equal(84, ProGameEvidence.Parse(initial)!.HomeProgress.Scheduled);
        Assert.True(ProGameEvidence.Parse(initial)!.VerifiedUnstarted);
        fail = true; clock.Now = clock.Now.AddMinutes(1);
        await collector.RefreshAsync(default); Assert.Equal(initial, game.ProEvidenceJson);
        fail = false; unusable = true;
        await collector.RefreshAsync(default); Assert.False(ProGameEvidence.Parse(game.ProEvidenceJson)!.VerifiedUnstarted);
        Assert.Equal(clock.Now.UtcDateTime, ProGameEvidence.Parse(game.ProEvidenceJson)!.ObservedAt);

        var oddsBody = ProHypeTests.Fixture("nhl-upcoming-board").GetRawText();
        using var oddsHandler = new ProFixtureHandler(_ => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(oddsBody) });
        using var oddsHttp = new HttpClient(oddsHandler);
        var espn = new backend.Services.Espn.EspnOddsService(oddsHttp, Microsoft.Extensions.Logging.Abstractions.NullLogger<backend.Services.Espn.EspnOddsService>.Instance, app.Services, null!, null!);
        var pregame = new PregameOddsService(app.Services, Microsoft.Extensions.Logging.Abstractions.NullLogger<PregameOddsService>.Instance, espn, clock);
        Assert.True(await pregame.RefreshPregameOddsForSportAsync("nhl", null) > 0);
        var quote = await db.PregameOdds.AsNoTracking().SingleAsync(o => o.GameId == game.Id);
        Assert.True(backend.Scoring.ProHypePolicy.EligibleOdds(game, quote, clock.Now.UtcDateTime));
        Assert.Equal(game.SeasonYear, quote.SourceSeason); Assert.Equal(game.HomeTeamAbbreviation, quote.HomeTeamAbbreviation);
        var emptyQuotes = System.Text.Json.Nodes.JsonNode.Parse(oddsBody)!;
        foreach (var ev in emptyQuotes["events"]!.AsArray()) ev!["competitions"]![0]!["odds"] = new System.Text.Json.Nodes.JsonArray();
        oddsBody = emptyQuotes.ToJsonString(); clock.Now = clock.Now.AddMinutes(1);
        await pregame.RefreshPregameOddsForSportAsync("nhl", null);
        quote = await db.PregameOdds.AsNoTracking().SingleAsync(o => o.GameId == game.Id);
        Assert.Null(quote.SourceSeason); Assert.Equal(clock.Now.UtcDateTime, quote.FetchedAt);
        Assert.False(backend.Scoring.ProHypePolicy.EligibleOdds(game, quote, clock.Now.UtcDateTime));

        var fpiBody = ProHypeTests.Fixture("nfl-fpi").GetRawText();
        using var fpiHandler = new ProFixtureHandler(_ => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(fpiBody) });
        using var fpiHttp = new HttpClient(fpiHandler);
        var index = new backend.Services.Espn.EspnPowerIndexService(fpiHttp, Microsoft.Extensions.Logging.Abstractions.NullLogger<backend.Services.Espn.EspnPowerIndexService>.Instance, app.Services, clock);
        Assert.Equal(32, await index.RefreshNflRatingsAsync(null));
        db.ChangeTracker.Clear();
        var rows = await db.TeamRatings.Where(r => r.Source == "espn_fpi").ToListAsync();
        Assert.All(rows, row => Assert.Equal(2026, row.SourceSeason));
        var wrong = System.Text.Json.Nodes.JsonNode.Parse(fpiBody)!; wrong["requestedSeason"]!["year"] = 2025; fpiBody = wrong.ToJsonString();
        Assert.Equal(0, await index.RefreshNflRatingsAsync(null)); db.ChangeTracker.Clear();
        rows = await db.TeamRatings.Where(r => r.Source == "espn_fpi").ToListAsync();
        Assert.All(rows, row => { Assert.Null(row.PowerRank); Assert.Null(row.SourceSeason); });
    }

    [Fact]
    public async Task Synthetic_NHL_stakes_flow_through_importers_shared_service_and_all_pages()
    {
        var capture = NhlStakesEvidenceTests.Capture;
        clock.Now = new DateTimeOffset(NhlStakesEvidenceTests.Now);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var game = NhlStakesEvidenceTests.SyntheticGame();
        db.Games.Add(game);
        await db.SaveChangesAsync();
        var responses = capture.GetProperty("schedules").EnumerateArray()
            .Concat(new[] { capture.GetProperty("roster"), capture.GetProperty("board"), capture.GetProperty("kalshi") })
            .ToDictionary(c => c.GetProperty("url").GetString()!, c => c.GetProperty("body").GetRawText());
        using var handler = new ProFixtureHandler(url => new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(url.Contains("/scoreboard")
                ? capture.GetProperty("board").GetProperty("body").GetRawText() : responses[url])
        });
        using var http = new HttpClient(handler);
        var competition = new ProCompetitionEvidenceService(http, db, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<ProCompetitionEvidenceService>.Instance);
        await competition.RefreshAsync(default);
        var qualification = new KalshiPlayoffRatingsService(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<KalshiPlayoffRatingsService>.Instance, app.Services, clock);
        Assert.Equal(23, await qualification.RefreshAsync("nhl", 2027, default));
        var espn = new backend.Services.Espn.EspnOddsService(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<backend.Services.Espn.EspnOddsService>.Instance, app.Services, null!, null!);
        var pregame = new PregameOddsService(app.Services, Microsoft.Extensions.Logging.Abstractions.NullLogger<PregameOddsService>.Instance, espn, clock);
        Assert.True(await pregame.RefreshPregameOddsForSportAsync("nhl", null) > 0);
        var evidence = ProGameEvidence.Parse(game.ProEvidenceJson)!;
        Assert.Null(evidence.HomeProgress.SeedingUnresolved);
        Assert.Null(evidence.AwayProgress.SeedingUnresolved);
        var odds = await db.PregameOdds.AsNoTracking().SingleAsync(o => o.GameId == game.Id);
        var rows = await db.TeamRatings.AsNoTracking().Where(r => r.Sport == "nhl").ToListAsync();
        Assert.Equal(.2, rows.Single(r => r.Source == "kalshi" && r.TeamAbbreviation == "NYI").PlayoffProbability);
        Assert.Equal(.55, rows.Single(r => r.Source == "kalshi" && r.TeamAbbreviation == "PHI").PlayoffProbability);
        var hype = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
        var direct = hype.CalculateFromEvidence(game, rows, odds, Array.Empty<string>(), clock.Now.UtcDateTime);
        var stored = await hype.GetHypeComponentsAsync(game, null);
        Assert.True(stored.StakesAvailable, stored.StakesExplanation);
        Assert.Equal(5.89172483871624, stored.StakesScore, 12);
        Assert.Equal(stored.StakesScore, direct.StakesScore);
        Assert.Equal(.35, direct.HomePlayoffOdds);
        Assert.Equal(.8, direct.AwayPlayoffOdds);
        Assert.False(stored.QualityAvailable);  
        Assert.Equal(80, Math.Round(stored.ClosenessScore));
        Assert.False(stored.IsComplete);

        var detail = await BodyText(await client.GetAsync("/game/" + game.Id));
        Assert.Contains("Hype unavailable", detail);
        Assert.Contains("Stakes6", detail);
        Assert.Contains("QualityUnavailable", detail);
        Assert.Contains("Closeness80", detail);
        Assert.Contains("qualification-only", stored.StakesExplanation);
        Assert.DoesNotContain("seeding", stored.StakesExplanation);
        Assert.Contains("1/84 completed", stored.StakesExplanation);
        Assert.Contains("0/84 completed", stored.StakesExplanation);
        Assert.Contains("2026-10-01", stored.StakesExplanation);
        foreach (var path in new[] { "/", "/nhl?date=2026-10-01" })
        {
            using var doc = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(await client.GetStringAsync(path), CancellationToken.None);
            Assert.Contains("Hype unavailable", doc.QuerySelector($"a[href='/game/{game.Id}']")!.TextContent);
        }

        using var moneyPuckHandler = new ProFixtureHandler(url => new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(MoneyPuckTests.Fixture(url == MoneyPuckRankings.RankingsUrl ? "rankings_current.csv" : "update_time.txt"))
        });
        using var moneyPuckHttp = new HttpClient(moneyPuckHandler);
        var moneyPuck = new MoneyPuckService(moneyPuckHttp, Microsoft.Extensions.Logging.Abstractions.NullLogger<MoneyPuckService>.Instance, app.Services, clock);
        Assert.Equal(32, await moneyPuck.RefreshNhlRatingsAsync(default));
        stored = await hype.GetHypeComponentsAsync(game, null);
        Assert.True(stored.IsComplete);
        Assert.Equal(44, Math.Round(stored.QualityScore));
        Assert.Equal(42, Math.Round((await hype.CalculateHypeScoreAsync(game, null))!.Value));
        detail = await BodyText(await client.GetAsync("/game/" + game.Id));
        Assert.DoesNotContain("Hype unavailable", detail);
        Assert.Contains("Quality44", detail);
        Assert.Contains("qualification-only", stored.StakesExplanation);
        foreach (var path in new[] { "/", "/nhl?date=2026-10-01" })
        {
            using var doc = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(await client.GetStringAsync(path), CancellationToken.None);
            Assert.DoesNotContain("Hype unavailable", doc.QuerySelector($"a[href='/game/{game.Id}']")!.TextContent);
        }

        var awaySchedule = capture.GetProperty("schedules").EnumerateArray().Single(c => c.GetProperty("body").GetProperty("team").GetProperty("abbreviation").GetString() == "TB");
        var invalid = System.Text.Json.Nodes.JsonNode.Parse(awaySchedule.GetProperty("body").GetRawText())!;
        invalid["timestamp"] = clock.Now.AddHours(-1).ToString("O");
        responses[awaySchedule.GetProperty("url").GetString()!] = invalid.ToJsonString();
        await competition.RefreshAsync(default);
        Assert.Null(ProGameEvidence.Parse(game.ProEvidenceJson)!.AwayProgress.Completed);
        stored = await hype.GetHypeComponentsAsync(game, null);
        Assert.False(stored.StakesAvailable);
        Assert.True(stored.ClosenessAvailable);
        detail = await BodyText(await client.GetAsync("/game/" + game.Id));
        Assert.Contains("StakesUnavailable", detail);
        Assert.Contains("Closeness80", detail);
    }

    [Fact]
    public async Task Synthetic_MLB_first_inning_odds_are_eligible_on_insert_and_refresh()
    {
        clock.Now = new DateTimeOffset(MlbPregameEvidenceTests.Now);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var game = MlbPregameEvidenceTests.SyntheticGame();
        db.Games.Add(game);
        await db.SaveChangesAsync();
        var original = MlbPregameEvidenceTests.Capture.GetProperty("board").GetProperty("body").GetRawText();
        var body = original;
        using var handler = new ProFixtureHandler(_ => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        using var http = new HttpClient(handler);
        var espn = new backend.Services.Espn.EspnOddsService(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<backend.Services.Espn.EspnOddsService>.Instance, app.Services, null!, null!);
        var importer = new PregameOddsService(app.Services, Microsoft.Extensions.Logging.Abstractions.NullLogger<PregameOddsService>.Instance, espn, clock);
        foreach (var variant in new[] { "later-inning", "suspended", "postponed", "completed", "scored" })
        {
            body = original;
            await importer.RefreshPregameOddsForSportAsync("mlb", null);
            var quote = await db.PregameOdds.AsNoTracking().SingleAsync(o => o.GameId == game.Id);
            Assert.True(backend.Scoring.ProHypePolicy.EligibleOdds(game, quote, clock.Now.UtcDateTime));
            Assert.Equal(-120, quote.HomeMoneyline);
            Assert.Equal(110, quote.AwayMoneyline);
            Assert.Equal(2026, quote.SourceSeason);
            var invalid = System.Text.Json.Nodes.JsonNode.Parse(original)!;
            MlbPregameEvidenceTests.Invalidate(invalid["events"]![0]!, variant);
            body = invalid.ToJsonString();
            await importer.RefreshPregameOddsForSportAsync("mlb", null);
            quote = await db.PregameOdds.AsNoTracking().SingleAsync(o => o.GameId == game.Id);
            Assert.Null(quote.SourceSeason);
            Assert.False(backend.Scoring.ProHypePolicy.EligibleOdds(game, quote, clock.Now.UtcDateTime));
        }
    }

    [Fact]
    public async Task Synthetic_MLB_preview_flows_through_importers_and_all_pages()
    {
        clock.Now = new DateTimeOffset(MlbPregameEvidenceTests.Now);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var game = MlbPregameEvidenceTests.SyntheticGame();
        db.Games.Add(game);
        var ranks = ProHypeTests.Ranks(game, 4, 8);
        db.TeamRatings.AddRange(ranks);
        await db.SaveChangesAsync();
        var capture = MlbPregameEvidenceTests.Capture;
        var schedules = capture.GetProperty("schedules").EnumerateArray()
            .ToDictionary(c => c.GetProperty("url").GetString()!, c => c.GetProperty("body").GetRawText());
        using var handler = new ProFixtureHandler(url => new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(url.Contains("/scoreboard")
                ? capture.GetProperty("board").GetProperty("body").GetRawText()
                : url.Contains("/schedule") ? schedules[url]
                : "{\"sports\":[{\"leagues\":[{\"season\":{\"year\":2026},\"teams\":[{\"team\":{\"id\":\"15\",\"abbreviation\":\"ATL\"}},{\"team\":{\"id\":\"22\",\"abbreviation\":\"PHI\"}}]}]}]}")
        });
        using var http = new HttpClient(handler);
        var competition = new ProCompetitionEvidenceService(http, db, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<ProCompetitionEvidenceService>.Instance);
        await competition.RefreshAsync(default);
        var espn = new backend.Services.Espn.EspnOddsService(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<backend.Services.Espn.EspnOddsService>.Instance, app.Services, null!, null!);
        var importer = new PregameOddsService(app.Services, Microsoft.Extensions.Logging.Abstractions.NullLogger<PregameOddsService>.Instance, espn, clock);
        await importer.RefreshPregameOddsForSportAsync("mlb", null);
        var hype = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
        var stored = await hype.GetHypeComponentsAsync(game, null);
        Assert.True(stored.IsComplete, stored.StakesExplanation + "; " + stored.ClosenessExplanation);
        Assert.Equal(85, stored.StakesScore);
        Assert.Equal(81, Math.Round(stored.QualityScore));
        Assert.Equal(93, Math.Round(stored.ClosenessScore));
        Assert.Equal(87, Math.Round((await hype.CalculateHypeScoreAsync(game, null))!.Value));
        var odds = await db.PregameOdds.AsNoTracking().SingleAsync(o => o.GameId == game.Id);
        var direct = hype.CalculateFromEvidence(game, ranks, odds, new[] { "401907965", "401907972" }, clock.Now.UtcDateTime);
        Assert.Equal(stored.StakesScore, direct.StakesScore);
        Assert.Equal(stored.ClosenessScore, direct.ClosenessScore);
        var detail = await BodyText(await client.GetAsync("/game/" + game.Id));
        Assert.DoesNotContain("Hype unavailable", detail);
        Assert.Contains("Stakes85", detail);
        Assert.Contains("Quality81", detail);
        Assert.Contains("Closeness93", detail);
        foreach (var path in new[] { "/", "/mlb?date=2026-10-01" })
        {
            using var doc = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(await client.GetStringAsync(path), CancellationToken.None);
            Assert.DoesNotContain("Hype unavailable", doc.QuerySelector($"a[href='/game/{game.Id}']")!.TextContent);
        }
    }

    [Fact]
    public async Task MoneyPuck_import_and_pages_preserve_publication_dates_and_do_not_fall_back_to_old_rankings()
    {
        clock.Now = new DateTimeOffset(ProHypeTests.Now);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var game = ProHypeTests.Game(); game.Id = "moneypuck-preview";
        game.HomeTeamAbbreviation = "NYR"; game.AwayTeamAbbreviation = "TB";
        var evidence = ProHypeTests.Evidence(game); evidence.HomeProgress.SeedingUnresolved = null;
        evidence.HomeProgress.Completed = null;  
        game.ProEvidenceJson = System.Text.Json.JsonSerializer.Serialize(evidence);
        db.Games.Add(game); db.PregameOdds.Add(ProHypeTests.Odds(game));
        db.TeamRatings.AddRange(ProHypeTests.Ranks(game, 1, 2, "powerrankingsguru"));
        foreach (var team in new[] { "NYR", "TB" })
            db.TeamRatings.Add(new TeamRating { Sport = "nhl", TeamAbbreviation = team, Source = "kalshi", Season = 2027,
                SourceSeason = 2027, PlayoffProbability = .5, FetchedAt = clock.Now.UtcDateTime });
        await db.SaveChangesAsync();

        var body = MoneyPuckTests.Fixture("rankings_current.csv");
        var updated = MoneyPuckTests.Fixture("update_time.txt");
        string? failedUrl = null;
        var race = false; var updateRequests = 0;
        using var handler = new ProFixtureHandler(url =>
        {
            Assert.Contains(url, new[] { MoneyPuckRankings.RankingsUrl, MoneyPuckRankings.UpdatedUrl });
            if (url == failedUrl) return new(System.Net.HttpStatusCode.ServiceUnavailable);
            if (url == MoneyPuckRankings.UpdatedUrl) updateRequests++;
            var text = url == MoneyPuckRankings.RankingsUrl ? body : updated;
            if (race && url == MoneyPuckRankings.UpdatedUrl && updateRequests % 2 == 0)
                text = "Wednesday September 30, 2026 at 2:49 PM Eastern Time";
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(text) };
        });
        using var http = new HttpClient(handler);
        var importer = new MoneyPuckService(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<MoneyPuckService>.Instance, app.Services, clock);
        Assert.Equal(32, await importer.RefreshNhlRatingsAsync(default));
        Assert.Equal(0, await importer.RefreshNhlRatingsAsync(default));
        var rows = await db.TeamRatings.AsNoTracking().Where(r => r.Source == "moneypuck").ToListAsync();
        Assert.Equal(32, rows.Count);
        Assert.All(rows, r => { Assert.Equal(2027, r.SourceSeason); Assert.Equal(MoneyPuckTests.Published, r.RankPublishedAt); Assert.Null(r.PlayoffProbability); });
        var rankInfo = await scope.ServiceProvider.GetRequiredService<GameDataService>().GetTeamRankInfoAsync("nhl", "TB", 2027, 2, default);
        Assert.Equal(5, rankInfo.Value.PowerRank.Value);
        var alias = await scope.ServiceProvider.GetRequiredService<GameDataService>().GetTeamRankInfoAsync("nhl", "UTA", 2027, 2, default);
        Assert.Equal(13, alias.Value.PowerRank.Value);
        var hype = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
        var components = await hype.GetHypeComponentsAsync(game, null);
        Assert.True(components.QualityAvailable); Assert.False(components.StakesAvailable);
        Assert.Equal(44, Math.Round(components.QualityScore));
        var detail = await BodyText(await client.GetAsync("/game/" + game.Id));
        Assert.Contains("moneypuck: #23 / #5", components.QualityExplanation);
        Assert.Contains("rolling ranking published 2026-09-27T16:00:00", components.QualityExplanation);
        Assert.Contains("Hype unavailable", detail); Assert.DoesNotContain("QualityUnavailable", detail);
        foreach (var path in new[] { "/", "/nhl?date=2026-09-30" })
        {
            using var doc = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(await client.GetStringAsync(path), CancellationToken.None);
            var card = doc.QuerySelector($"a[href='/game/{game.Id}']")!.TextContent;
            Assert.Contains("5th in NHL", card); Assert.Contains("23rd in NHL", card);
        }

        clock.Now = clock.Now.AddMinutes(1);
        foreach (var url in new[] { MoneyPuckRankings.RankingsUrl, MoneyPuckRankings.UpdatedUrl })
        {
            failedUrl = url;
            clock.Now = clock.Now.AddHours(24);
            Assert.Equal(0, await importer.RefreshNhlRatingsAsync(default));
            Assert.All(await db.TeamRatings.AsNoTracking().Where(r => r.Source == "moneypuck").ToListAsync(), r => Assert.Equal(ProHypeTests.Now, r.FetchedAt));
        }
        failedUrl = null; race = true; updateRequests = 0;
        clock.Now = clock.Now.AddHours(24);
        Assert.Equal(0, await importer.RefreshNhlRatingsAsync(default));
        Assert.All(await db.TeamRatings.AsNoTracking().Where(r => r.Source == "moneypuck").ToListAsync(), r => Assert.Equal(ProHypeTests.Now, r.FetchedAt));
        race = false;
        body = "<html>Unavailable</html>";
        clock.Now = clock.Now.AddHours(24);
        Assert.Equal(0, await importer.RefreshNhlRatingsAsync(default));
        rows = await db.TeamRatings.AsNoTracking().Where(r => r.Source == "moneypuck").ToListAsync();
        Assert.All(rows, r => { Assert.Null(r.PowerRank); Assert.Null(r.SourceSeason); Assert.Null(r.RankPublishedAt); Assert.Equal(clock.Now.UtcDateTime, r.FetchedAt); });
        detail = await BodyText(await client.GetAsync("/game/" + game.Id));
        Assert.Contains("QualityUnavailable", detail); Assert.DoesNotContain("powerrankingsguru", detail);
        db.ChangeTracker.Clear();  
        rankInfo = await scope.ServiceProvider.GetRequiredService<GameDataService>().GetTeamRankInfoAsync("nhl", "TB", 2027, 2, default);
        Assert.Null(rankInfo.Value.PowerRank);
        body = MoneyPuckTests.Fixture("rankings_current.csv");
        clock.Now = clock.Now.AddHours(24);
        updated = "Monday October 5, 2026 at 2:49 PM Eastern Time";
        Assert.Equal(32, await importer.RefreshNhlRatingsAsync(default));
        clock.Now = new DateTimeOffset(new DateTime(2026, 10, 5, 18, 49, 0, DateTimeKind.Utc).AddDays(7));
        Assert.Equal(0, await importer.RefreshNhlRatingsAsync(default));
        rows = await db.TeamRatings.AsNoTracking().Where(r => r.Source == "moneypuck").ToListAsync();
        Assert.All(rows, r =>
        {
            Assert.NotNull(r.PowerRank);
            Assert.Equal(2027, r.SourceSeason);
            Assert.Equal(new DateTime(2026, 10, 5, 18, 49, 0, DateTimeKind.Utc), r.RankPublishedAt);
            Assert.Equal(clock.Now.UtcDateTime, r.FetchedAt);
            Assert.False(ProHypePolicy.EligibleRank(r, "nhl", 2027, clock.Now.UtcDateTime));
        });
        Assert.Null(MoneyPuckRankings.Parse(body, updated, clock.Now.UtcDateTime));
        Assert.Null(ProHypePolicy.Quality(game, rows, clock.Now.UtcDateTime).Value);
        db.ChangeTracker.Clear();
        rankInfo = await scope.ServiceProvider.GetRequiredService<GameDataService>().GetTeamRankInfoAsync("nhl", "TB", 2027, 2, default);
        Assert.Null(rankInfo.Value.PowerRank);
    }

    [Fact]
    public async Task MoneyPuck_migration_is_repeatable_and_leaves_existing_publication_unknown()
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE team_ratings DROP COLUMN rank_published_at");
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "029_MoneyPuckPublication.sql"));
        await db.Database.ExecuteSqlRawAsync(sql); await db.Database.ExecuteSqlRawAsync(sql);
        Assert.All(await db.TeamRatings.AsNoTracking().ToListAsync(), r => Assert.Null(r.RankPublishedAt));
    }

    [Fact]
    public async Task Pro_evidence_migration_is_repeatable_and_does_not_verify_legacy_data()
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE games DROP COLUMN pro_evidence_json; ALTER TABLE pregame_odds DROP COLUMN source_season; ALTER TABLE pregame_odds DROP COLUMN applicable_start; ALTER TABLE pregame_odds DROP COLUMN home_team_abbreviation; ALTER TABLE pregame_odds DROP COLUMN away_team_abbreviation");
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "028_ProHypeEvidence.sql"));
        await db.Database.ExecuteSqlRawAsync(sql); await db.Database.ExecuteSqlRawAsync(sql);
        Assert.All(await db.Games.AsNoTracking().ToListAsync(), g => Assert.Null(g.ProEvidenceJson));
        Assert.All(await db.PregameOdds.AsNoTracking().ToListAsync(), o => { Assert.Null(o.SourceSeason); Assert.Null(o.ApplicableStart); Assert.Null(o.HomeTeamAbbreviation); Assert.Null(o.AwayTeamAbbreviation); });
    }

    private static void AssertContainsPercent(int percent, string text) =>
        Assert.Contains($"{percent}%", System.Text.RegularExpressions.Regex.Replace(text, @"\s+", ""));

    private static void AssertNoScoringDiagnostics(string html)
    {
        foreach (var forbidden in new[] { "detail-data", "source season", "rolling ranking published", "seeding remains unknown",
            "seeding ceiling", "qualification term", "Exact Stakes:", "Last fetched", "SERVER_ONLY_COMPETITION_EVIDENCE",
            "apis/site/v2", "Qualification probability unavailable, invalid, or expired", "proEvidenceJson",
            "stakesExplanation", "qualityExplanation", "closenessExplanation" })
            Assert.DoesNotContain(forbidden, html);
    }

    private static async Task<string> BodyText(HttpResponseMessage response)
    {
        var html = await response.Content.ReadAsStringAsync();
        AssertNoScoringDiagnostics(html);  
        using var document = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(html, CancellationToken.None);
        return document.Body!.TextContent;
    }

    [Fact]
    public async Task Homepage_does_not_mix_the_same_teams_across_seasons()
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        db.Games.Add(new Game
        {
            Id = "nfl-old", Sport = "nfl", HomeTeam = "Kansas City Chiefs", AwayTeam = "Buffalo Bills",
            HomeTeamAbbreviation = "KC", AwayTeamAbbreviation = "BUF", SeasonYear = 2025,
            SeasonType = 2, WeekNumber = 1, Status = "in_progress", ScheduledStart = clock.Now.UtcDateTime
        });
        db.TeamRatings.Add(new TeamRating
        {
            Sport = "nfl", TeamAbbreviation = "KC", Source = "kalshi", Season = 2025,
            SourceSeason = 2025, PlayoffProbability = 0.23, FetchedAt = clock.Now.UtcDateTime
        });
        await db.SaveChangesAsync();
        var html = await client.GetStringAsync("/");
        using var document = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(html, CancellationToken.None);
        var current = document.QuerySelector("a[href='/game/nfl-current']")!.TextContent;
        var old = document.QuerySelector("a[href='/game/nfl-old']")!.TextContent;
        Assert.Matches(@"(?<!\d)64\s*% playoff odds", current);
        Assert.DoesNotMatch(@"(?<!\d)23\s*% playoff odds", current);
        Assert.Matches(@"(?<!\d)23\s*% playoff odds", old);
        Assert.DoesNotMatch(@"(?<!\d)64\s*% playoff odds", old);
    }

    [Fact]
    public void Production_DI_resolves_rating_clients_and_workers()
    {
        using var scope = app.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<PowerRankingsGuruService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<MoneyPuckService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<KalshiPlayoffRatingsService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<GameDataService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<HypeScoreService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ProCompetitionEvidenceService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<backend.Services.Espn.EspnPowerIndexService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<PregameOddsService>());
        Assert.NotNull(ActivatorUtilities.CreateInstance<ProCompetitionRefreshBackgroundService>(scope.ServiceProvider));
        Assert.NotNull(ActivatorUtilities.CreateInstance<KalshiPlayoffRefreshBackgroundService>(scope.ServiceProvider));
    }

    [Fact]
    public async Task Playoff_evidence_migration_is_repeatable_and_preserves_provider_snapshot_uniqueness()
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE team_ratings");
        foreach (var name in new[] { "002_TeamRatings.sql", "025_TeamRatingSourceSeason.sql", "026_PlayoffFinalOutcome.sql", "029_MoneyPuckPublication.sql" })
            await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", name)));
        await db.Database.ExecuteSqlRawAsync("INSERT INTO team_ratings (sport, team_abbreviation, season, source, playoff_probability) VALUES ('nhl', 'BOS', 2027, 'kalshi', 1)");
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "029_PlayoffQuoteEvidence.sql"));
        await db.Database.ExecuteSqlRawAsync(sql);
        await db.Database.ExecuteSqlRawAsync(sql);
        var legacy = await db.TeamRatings.AsNoTracking().SingleAsync();
        Assert.Null(legacy.SourceSeason);
        Assert.False(legacy.PlayoffProbabilityIsFinal);
        Assert.Equal(1, legacy.PlayoffProbability);
        Assert.Null(await db.TeamRatings.Select(r => EF.Property<string>(r, "PlayoffEvidenceJson")).SingleAsync());
        await db.Database.ExecuteSqlRawAsync("INSERT INTO team_ratings (sport, team_abbreviation, season, source) VALUES ('nhl', 'BOS', 2027, 'polymarket')");
        var duplicate = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO team_ratings (sport, team_abbreviation, season, source) VALUES ('nhl', 'BOS', 2027, 'polymarket')"));
        Assert.Equal("23505", duplicate.SqlState);
    }

    [Fact]
    public async Task Source_season_migration_is_repeatable_and_leaves_legacy_rows_unverified()
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE team_ratings DROP COLUMN source_season");
        var path = Path.Combine(AppContext.BaseDirectory, "Migrations", "025_TeamRatingSourceSeason.sql");
        var sql = await File.ReadAllTextAsync(path);
        await db.Database.ExecuteSqlRawAsync(sql);
        await db.Database.ExecuteSqlRawAsync(sql);
        var rows = await db.TeamRatings.AsNoTracking().ToListAsync();
        Assert.Equal(4, rows.Count);
        Assert.All(rows, row => Assert.Null(row.SourceSeason));
    }

    [Theory]
    [InlineData("nba", "BOS", "NYK", 2027)]
    [InlineData("nhl", "BOS", "TOR", 2027)]
    [InlineData("mlb", "LAD", "NYY", 2026)]
    public async Task All_league_pages_keep_seasons_separate_and_show_source_and_expiry(
        string sport, string home, string away, int season)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        foreach (var (year, suffix, probability) in new[] { (season, "current", 0.42), (season - 1, "old", 0.23) })
        {
            var id = $"{sport}-{suffix}";
            db.Games.Add(new Game
            {
                Id = id, Sport = sport, HomeTeam = home, AwayTeam = away,
                HomeTeamAbbreviation = home, AwayTeamAbbreviation = away, SeasonYear = year,
                SeasonType = 2, Status = "scheduled", ScheduledStart = clock.Now.UtcDateTime
            });
            db.PregameOdds.Add(new PregameOdds { GameId = id, HomeWinProbability = 0.5, AwayWinProbability = 0.5 });
            foreach (var team in new[] { home, away })
            {
                db.TeamRatings.Add(new TeamRating
                {
                    Sport = sport, TeamAbbreviation = team, Source = "kalshi", Season = year,
                    SourceSeason = year, PlayoffProbability = probability, FetchedAt = clock.Now.UtcDateTime
                });
                db.TeamRatings.Add(new TeamRating
                {
                    Sport = sport, TeamAbbreviation = team, Source = sport == "nhl" ? "moneypuck" : "powerrankingsguru", Season = year,
                    SourceSeason = year, PlayoffProbability = 0.99, PowerRank = 18, ChampionshipProbability = 0.1,
                    RankPublishedAt = sport == "nhl" ? clock.Now.UtcDateTime : null, FetchedAt = clock.Now.UtcDateTime
                });
            }
        }
        await db.SaveChangesAsync();
        foreach (var path in new[] { "/", $"/{sport}?date=2026-09-13" })
        {
            var html = await client.GetStringAsync(path);
            using var document = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(html, CancellationToken.None);
            var current = document.QuerySelector($"a[href='/game/{sport}-current']")!.TextContent;
            var old = document.QuerySelector($"a[href='/game/{sport}-old']")!.TextContent;
            Assert.Matches(@"(?<!\d)42\s*% playoff odds", current);
            Assert.DoesNotMatch(@"(?<!\d)23\s*% playoff odds", current);
            Assert.Matches(@"(?<!\d)23\s*% playoff odds", old);
            Assert.DoesNotMatch(@"(?<!\d)42\s*% playoff odds", old);
        }
        var detail = await BodyText(await client.GetAsync($"/game/{sport}-current"));
        if (sport == "nhl") Assert.DoesNotContain("Kalshi", detail);
        else Assert.Contains("Kalshi", detail);
        Assert.DoesNotContain("2026-09-13 18:00 UTC", detail);
        Assert.Contains("#18", detail);
        Assert.Matches(@"(?<!\d)42\s*%", detail);
        clock.Now = clock.Now.AddHours(1);
        detail = await BodyText(await client.GetAsync($"/game/{sport}-current"));
        Assert.Contains("Unavailable", detail);
        Assert.DoesNotMatch(@"(?<!\d)42\s*%", detail);
        Assert.Contains("#18", detail);
    }

    [Fact]
    public async Task Final_outcome_migration_is_repeatable_without_promoting_extreme_prices()
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.TeamRatings.Where(r => r.Source == "kalshi").ExecuteUpdateAsync(
            setters => setters.SetProperty(r => r.PlayoffProbability, 1.0));
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE team_ratings DROP COLUMN playoff_probability_is_final");
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "026_PlayoffFinalOutcome.sql"));
        await db.Database.ExecuteSqlRawAsync(sql);
        await db.Database.ExecuteSqlRawAsync(sql);
        var rows = await db.TeamRatings.AsNoTracking().ToListAsync();
        Assert.Equal(4, rows.Count);
        Assert.All(rows, row => Assert.False(row.PlayoffProbabilityIsFinal));
    }

    [Theory]
    [InlineData("nfl", "KC", "BUF", 2026, true)]
    [InlineData("nba", "BOS", "NYK", 2027, true)]
    [InlineData("nhl", "BOS", "TOR", 2027, true)]
    [InlineData("mlb", "LAD", "NYY", 2026, true)]
    [InlineData("nfl", "KC", "BUF", 2026, false)]
    [InlineData("nba", "BOS", "NYK", 2027, false)]
    [InlineData("nhl", "BOS", "TOR", 2027, false)]
    [InlineData("mlb", "LAD", "NYY", 2026, false)]
    public async Task Pages_distinguish_confirmed_outcomes_from_extreme_estimates(
        string sport, string home, string away, int season, bool isFinal)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var gameId = $"outlook-{sport}";
        db.Games.Add(new Game
        {
            Id = gameId, Sport = sport, HomeTeam = home, AwayTeam = away,
            HomeTeamAbbreviation = home, AwayTeamAbbreviation = away, SeasonYear = season,
            SeasonType = 2, WeekNumber = 1, Status = "scheduled", ScheduledStart = clock.Now.UtcDateTime
        });
        db.PregameOdds.Add(new PregameOdds { GameId = gameId, HomeWinProbability = 0.5, AwayWinProbability = 0.5 });
        foreach (var (team, probability) in new[] { (home, 1.0), (away, 0.0) })
        {
            var rating = await db.TeamRatings.SingleOrDefaultAsync(r => r.Source == "kalshi"
                && r.Sport == sport && r.TeamAbbreviation == team && r.Season == season);
            if (rating is null)
            {
                rating = new TeamRating { Sport = sport, TeamAbbreviation = team, Source = "kalshi", Season = season };
                db.TeamRatings.Add(rating);
            }
            rating.SourceSeason = season;
            rating.PlayoffProbability = probability;
            rating.PlayoffProbabilityIsFinal = isFinal;
            rating.FetchedAt = clock.Now.UtcDateTime;
            rating.PlayoffEvidenceJson = isFinal ? KalshiFinalEvidence(sport, team, season, probability, clock.Now.UtcDateTime) : null;
        }
        await db.SaveChangesAsync();

        var leaguePath = sport == "nfl" ? $"/nfl?week=1&season={season}&type=2" : $"/{sport}?date=2026-09-13";
        foreach (var path in new[] { "/", leaguePath, $"/game/{gameId}" })
        {
            var html = await client.GetStringAsync(path);
            using var document = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(html, CancellationToken.None);
            var isDetail = path.StartsWith("/game/", StringComparison.Ordinal);
            var text = isDetail ? document.Body!.TextContent : document.QuerySelector($"a[href='/game/{gameId}']")!.TextContent;
            if (isFinal)
            {
                Assert.Equal(1, text.Split("Clinched playoffs").Length - 1);
                Assert.Equal(1, text.Split("Missed playoffs").Length - 1);
                Assert.DoesNotContain("Clinched playoffs playoff odds", text);
                Assert.DoesNotContain("Missed playoffs playoff odds", text);
                Assert.DoesNotContain(">99%", text);
                Assert.DoesNotContain("<1%", text);
            }
            else
            {
                Assert.Contains(isDetail ? ">99%" : ">99% playoff odds", text);
                Assert.Contains(isDetail ? "<1%" : "<1% playoff odds", text);
                Assert.DoesNotContain("Clinched playoffs", text);
                Assert.DoesNotContain("Missed playoffs", text);
            }
            Assert.DoesNotMatch(@"(?<!\d)(?:0|100)\s*%", text);
            if (isDetail)
            {
                Assert.Equal(1, text.Split("Playoff outlook").Length - 1);
                Assert.DoesNotContain("Playoff Odds", text);
                Assert.DoesNotContain("Final outcome", text);
                Assert.DoesNotContain("Qualified", text);
                Assert.DoesNotContain("Did not qualify", text);
                if (sport == "nhl") Assert.DoesNotContain("Kalshi", text);
                else Assert.Contains("Kalshi", text);
                Assert.DoesNotContain("2026-09-13 18:00 UTC", text);
            }
        }
    }

    [Fact]
    public async Task Confirmed_outcomes_remain_visible_after_expiry_but_estimates_do_not()
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var home = await db.TeamRatings.SingleAsync(r => r.Source == "kalshi" && r.TeamAbbreviation == "KC");
        home.PlayoffProbability = 0;
        home.PlayoffProbabilityIsFinal = true;
        home.PlayoffEvidenceJson = KalshiFinalEvidence("nfl", "KC", 2026, 0, home.FetchedAt);
        await db.SaveChangesAsync();
        var detail = await BodyText(await client.GetAsync("/game/nfl-current"));
        Assert.Contains("Playoff outlook", detail);
        Assert.Contains("Missed playoffs", detail);
        Assert.Matches(@"(?<!\d)64\s*%", detail);
        Assert.DoesNotContain("Final outcome", detail);

        clock.Now = clock.Now.AddDays(2);
        detail = await BodyText(await client.GetAsync("/game/nfl-current"));
        Assert.Contains("Missed playoffs", detail);
        Assert.DoesNotMatch(@"(?<!\d)0\s*%", detail);
        Assert.DoesNotMatch(@"(?<!\d)64\s*%", detail);
        Assert.DoesNotContain("Final outcome", detail);
        Assert.Contains("Unavailable", detail);
        Assert.DoesNotContain("2026-09-13 18:00 UTC", detail);
    }

    [Theory]
    [InlineData("nba")]
    [InlineData("nhl")]
    public async Task Missing_game_season_uses_that_leagues_date_convention(string sport)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        db.Games.Add(new Game
        {
            Id = "no-season", Sport = sport, HomeTeam = "Boston", AwayTeam = "New York",
            HomeTeamAbbreviation = "BOS", AwayTeamAbbreviation = "NYK", SeasonType = 2,
            Status = "scheduled", ScheduledStart = new DateTime(2026, 1, 15, 18, 0, 0, DateTimeKind.Utc)
        });
        db.TeamRatings.Add(new TeamRating
        {
            Sport = sport, TeamAbbreviation = "BOS", Source = "kalshi", Season = 2026,
            SourceSeason = 2026, PlayoffProbability = 1, PlayoffProbabilityIsFinal = true,
            FetchedAt = clock.Now.UtcDateTime,
            PlayoffEvidenceJson = KalshiFinalEvidence(sport, "BOS", 2026, 1, clock.Now.UtcDateTime)
        });
        await db.SaveChangesAsync();
        var detail = await BodyText(await client.GetAsync("/game/no-season"));
        Assert.Contains("Clinched playoffs", detail);
        Assert.DoesNotMatch(@"(?<!\d)100\s*%", detail);
        Assert.DoesNotContain("Final outcome", detail);
        Assert.Contains("Unavailable", detail);
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (app is not null) await app.DisposeAsync();
        await entryPoint.DisposeAsync();
        await database.DisposeAsync();
    }
}
