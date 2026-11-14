using System.Net;
using System.Text;
using System.Text.Json;
using Backend.Rendering;
using backend.Data;
using backend.GraphQL.Types;
using backend.Models;
using backend.Services;
using backend.Services.Espn;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;
using Xunit;

namespace backend.tests;

public sealed class PreseasonVisibilityTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer database = IsolatedPostgreSql.CreateContainer();
    private readonly AppFactory entryPoint = new();
    private readonly Clock clock = new();
    private readonly ScoreboardHandler provider = new();
    private WebApplicationFactory<Program> app = null!;
    private HttpClient client = null!;

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 9, 20, 0, 0, TimeSpan.Zero);
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
            services.AddHttpClient<EspnOddsService>().ConfigurePrimaryHttpMessageHandler(() => provider);
        }));
        client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var scope = app.Services.CreateScope();
        await TestSchema.CreateAsync(scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database);
    }

    [Fact]
    public async Task Nba_date_page_omits_confirmed_preseason_game()
    {
        var preseason = Game("401898395", "nba", 1, "final");
        preseason.ScheduledStart = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        await Store(preseason, Game("regular", "nba", 2));

        var html = await client.GetStringAsync("/nba?date=2026-10-09");
        Assert.DoesNotContain($"/game/{preseason.Id}", html);
        Assert.Contains("/game/regular", html);
    }

    [Theory]
    [InlineData("nba", "/nba?date=2026-10-09")]
    [InlineData("nhl", "/nhl?date=2026-10-09")]
    [InlineData("mlb", "/mlb?date=2026-10-09")]
    [InlineData("nfl", "/nfl?week=1&season=2027&type=1")]
    [InlineData("cfb", "/cfb?week=1&season=2027&type=1")]
    public async Task Public_lists_exclude_preseason_for_every_league_and_game_status(string sport, string leaguePath)
    {
        var hidden = new[] { "scheduled", "in_progress", "final" }
            .Select(status => Game($"preseason-{status}", sport, 1, status)).ToArray();
        await Store([.. hidden, Game("regular", sport, 2, "in_progress"), Game("unclassified", sport, null)]);

        foreach (var path in new[] { "/", leaguePath, "/api/games", $"/api/games?sport={sport}" })
        {
            var body = await client.GetStringAsync(path);
            foreach (var game in hidden) Assert.DoesNotContain(game.Id, body);
        }
        var home = await client.GetStringAsync("/");
        Assert.Contains("/game/regular", home);
        Assert.Contains("/game/unclassified", home);
        if (sport is "nfl" or "cfb")
            Assert.Contains("No", await client.GetStringAsync(leaguePath));
    }

    [Fact]
    public async Task Protected_competitions_and_missing_classifications_remain_public()
    {
        var games = new[] { (int?)null, 0, 2, 3, 4, 5, 6 }
            .Select(phase => Game($"phase-{phase?.ToString() ?? "missing"}", "nba", phase)).ToArray();
        await Store(games);
        foreach (var path in new[] { "/", "/nba?date=2026-10-09", "/api/games" })
        {
            var body = await client.GetStringAsync(path);
            foreach (var game in games) Assert.Contains(game.Id, body);
        }
        foreach (var game in games)
        {
            using var detail = await client.GetAsync($"/game/{game.Id}");
            using var odds = await client.GetAsync($"/api/games/{game.Id}/odds");
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            Assert.Equal(HttpStatusCode.OK, odds.StatusCode);
        }
    }

    [Fact]
    public async Task Summer_league_identifiers_are_hidden_even_without_a_preseason_phase()
    {
        var games = new[] { "nba-summer-las-vegas", "nba-summer-utah", "nba-summer-sacramento" }
            .Select((sport, i) => Game(sport, sport, i == 2 ? null : i + 2)).ToArray();
        await Store([.. games, Game("regular", "nba", 2)]);
        var body = await client.GetStringAsync("/api/games");
        Assert.Contains("regular", body);
        using var scope = app.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<GameDataService>();
        foreach (var game in games)
        {
            Assert.DoesNotContain(game.Id, body);
            Assert.Null(await service.GetGameAsync(game.Id, default));
            Assert.Empty(await service.GetGamesByDateRangeAsync(game.Sport, clock.Now.UtcDateTime.AddDays(-1), clock.Now.UtcDateTime.AddDays(1), default));
            Assert.Null(await service.GetCurrentWeekAsync(game.Sport, default));
        }
    }

    [Fact]
    public async Task Public_filter_runs_before_query_limits_and_keeps_data_intact()
    {
        var preseason = Enumerable.Range(0, 105).Select(i => Game($"preseason-{i}", "nba", 1)).ToArray();
        var regular = Game("regular", "nba", 2);
        regular.ScheduledStart = clock.Now.UtcDateTime.AddHours(-1);
        await Store([.. preseason, regular]);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        db.OddsSnapshots.Add(new OddsSnapshot { GameId = preseason[0].Id, Timestamp = clock.Now.UtcDateTime });
        await db.SaveChangesAsync();

        using var response = await client.GetAsync("/api/games?sport=nba");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("regular", Assert.Single(json.RootElement.EnumerateArray()).GetProperty("id").GetString());
        var service = scope.ServiceProvider.GetRequiredService<GameDataService>();
        Assert.Equal("regular", Assert.Single(await service.GetGamesByFilterAsync("nba", null, null, null, default)).Id);
        Assert.Equal(106, await db.Games.CountAsync());
        Assert.Equal(1, await db.OddsSnapshots.CountAsync());
    }

    [Theory]
    [InlineData("scheduled")]
    [InlineData("in_progress")]
    [InlineData("final")]
    public async Task Preseason_detail_odds_and_stream_links_return_not_found(string status)
    {
        await Store(Game("preseason", "nba", 1, status));
        foreach (var path in new[] { "/game/preseason", "/api/games/preseason/odds", "/sse/game/preseason" })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task Football_default_week_and_navigation_never_select_preseason()
    {
        var preseason = Game("preseason", "nfl", 1);
        var regular = Game("regular", "nfl", 2);
        regular.ScheduledStart = clock.Now.UtcDateTime.AddDays(4);
        await Store(preseason, regular);
        using var scope = app.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<GameDataService>();
        Assert.Equal(2, (await service.GetCurrentWeekAsync("nfl", default)).Value.SeasonType);
        var navigation = await service.GetAdjacentWeeksAsync("nfl", 2027, 1, 1, default);
        Assert.Null(navigation.PrevWeek);
        Assert.Null(navigation.NextWeek);
        Assert.DoesNotContain("Preseason", await client.GetStringAsync("/nfl"));

        clock.Now = clock.Now.AddMonths(1);
        Assert.Equal(2, (await service.GetCurrentWeekAsync("nfl", default)).Value.SeasonType);
        using var dbScope = app.Services.CreateScope();
        var db = dbScope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var laterPreseason = Game("later-preseason", "nfl", 1);
        laterPreseason.ScheduledStart = clock.Now.UtcDateTime.AddDays(-9);
        db.Games.Add(laterPreseason);
        await db.SaveChangesAsync();
        Assert.Equal(2, (await service.GetCurrentWeekAsync("nfl", default)).Value.SeasonType);
    }

    [Fact]
    public async Task Preseason_only_dates_use_the_existing_empty_state()
    {
        await Store(Game("preseason", "nba", 1));
        var html = await client.GetStringAsync("/nba?date=2026-10-09");
        Assert.Contains("No NBA games found.", html);
        Assert.DoesNotContain("/game/preseason", html);
        Assert.Contains("/nba?date=2026-10-08", html);
        Assert.Contains("/nba?date=2026-10-10", html);
    }

    [Fact]
    public async Task Live_publication_suppresses_preseason_on_every_topic_but_keeps_unclassified_games()
    {
        await Store(Game("preseason", "nba", 1), Game("regular", "nba", 2), Game("unclassified", "nba", null));
        var broadcaster = app.Services.GetRequiredService<ISseBroadcaster>();
        var publisher = app.Services.GetRequiredService<IGameUpdateService>();
        var topics = new[] { "live", "live:nba", "game:preseason", "game:regular", "game:unclassified" };
        var readers = topics.ToDictionary(topic => topic, broadcaster.Subscribe);
        try
        {
            foreach (var id in new[] { "preseason", "regular", "unclassified" })
                await publisher.PublishUpdateAsync(Update(id));
            foreach (var topic in new[] { "live", "live:nba" })
            {
                var reader = readers[topic];
                Assert.True(reader.TryRead(out var regular));
                Assert.Equal("regular", regular.GameId);
                Assert.True(reader.TryRead(out var unclassified));
                Assert.Equal("unclassified", unclassified.GameId);
                Assert.False(reader.TryRead(out _));
            }
            Assert.False(readers["game:preseason"].TryRead(out _));
            Assert.True(readers["game:regular"].TryRead(out _));
            Assert.True(readers["game:unclassified"].TryRead(out _));
        }
        finally
        {
            foreach (var topic in topics) broadcaster.Unsubscribe(topic, readers[topic]);
        }
    }

    [Theory]
    [InlineData(1, "preseason", "nba")]
    [InlineData(2, "regular-season", "nba-summer-las-vegas")]
    [InlineData(3, "postseason", "nba-summer-utah")]
    [InlineData(0, "preseason", "nba")]
    public async Task Collector_keeps_confirmed_warmups_in_storage_but_not_public_output(int phase, string slug, string league)
    {
        await Store(Game("provider-game", "nba", 2));
        provider.Payload = Scoreboard(phase, slug, league);
        using var scope = app.Services.CreateScope();
        var collector = scope.ServiceProvider.GetRequiredService<EspnOddsService>();
        await collector.ScrapeAndStoreOddsAsync("nba", default);
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        Assert.Equal(1, (await db.Games.SingleAsync()).SeasonType);
        Assert.DoesNotContain("provider-game", await client.GetStringAsync("/api/games"));
    }

    [Fact]
    public async Task Pregame_collector_uses_provider_fallback_to_classify_new_and_existing_games()
    {
        var existing = Game("provider-game", "nba", 0);
        await Store(existing);
        provider.Payload = Scoreboard(0, null, "nba", "pre", includeEventSeason: false, fallbackPhase: 1);
        using var scope = app.Services.CreateScope();
        var collector = scope.ServiceProvider.GetRequiredService<PregameOddsService>();
        await collector.RefreshPregameOddsForSportAsync("nba", default);
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        Assert.Equal(1, (await db.Games.SingleAsync()).SeasonType);
        Assert.Equal(1, await db.PregameOdds.CountAsync());
        Assert.DoesNotContain("provider-game", await client.GetStringAsync("/api/games"));

        db.PregameOdds.RemoveRange(db.PregameOdds);
        db.Games.RemoveRange(db.Games);
        await db.SaveChangesAsync();
        await collector.RefreshPregameOddsForSportAsync("nba", default);
        Assert.Equal(1, (await db.Games.SingleAsync()).SeasonType);
        Assert.Equal(1, await db.PregameOdds.CountAsync());
    }

    [Fact]
    public async Task Provider_without_competition_metadata_remains_visible()
    {
        provider.Payload = Scoreboard(0, null, "nba");
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EspnOddsService>().ScrapeAndStoreOddsAsync("nba", default);
        Assert.Contains("provider-game", await client.GetStringAsync("/api/games"));
    }

    private static GameUpdate Update(string id) => new()
    {
        GameId = id, Sport = Sport.NBA, HomeTeam = "Dallas Mavericks", AwayTeam = "Houston Rockets",
        HomeTeamAbbreviation = "DAL", AwayTeamAbbreviation = "HOU", Status = GameStatus.IN_PROGRESS
    };

    private string Scoreboard(int phase, string? slug, string league, string state = "post", bool includeEventSeason = true, int fallbackPhase = 0) => JsonSerializer.Serialize(new
    {
        season = new { year = 2027, type = fallbackPhase },
        leagues = new[] { new { id = "46", slug = league } },
        events = new[]
        {
            new
            {
                id = "provider-game", shortName = "HOU @ DAL", date = clock.Now.UtcDateTime.AddHours(1),
                season = includeEventSeason ? new { year = 2027, type = phase, slug } : null,
                status = new { type = new { state, name = state == "pre" ? "STATUS_SCHEDULED" : "STATUS_FINAL", completed = state == "post" }, period = state == "pre" ? 0 : 4 },
                competitions = new[]
                {
                    new
                    {
                        id = "provider-game", type = new { abbreviation = "STD" },
                        competitors = new[]
                        {
                            new { homeAway = "home", score = "0", team = new { id = "6", displayName = "Dallas Mavericks", abbreviation = "DAL" } },
                            new { homeAway = "away", score = "0", team = new { id = "10", displayName = "Houston Rockets", abbreviation = "HOU" } }
                        },
                        odds = new[] { new { moneyline = new { home = new { close = new { odds = "-110" } }, away = new { close = new { odds = "+100" } } } } }
                    }
                }
            }
        }
    });

    private sealed class ScoreboardHandler : HttpMessageHandler
    {
        public string Payload { get; set; } = "{\"events\":[]}";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Payload, Encoding.UTF8, "application/json")
            });
    }

    private Game Game(string id, string sport, int? phase, string status = "scheduled") => new()
    {
        Id = id, Sport = sport, HomeTeam = "Dallas Mavericks", AwayTeam = "Houston Rockets",
        HomeTeamAbbreviation = "DAL", AwayTeamAbbreviation = "HOU", SeasonYear = 2027,
        SeasonType = phase, ScheduledStart = clock.Now.UtcDateTime, Status = status,
        WeekNumber = 1, WeekLabel = phase == 1 ? "Preseason Week 1" : "Week 1"
    };

    private async Task Store(params Game[] games)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        db.Games.AddRange(games);
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (app is not null) await app.DisposeAsync();
        await entryPoint.DisposeAsync();
        await database.DisposeAsync();
    }
}
