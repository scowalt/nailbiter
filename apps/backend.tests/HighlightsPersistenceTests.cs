using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using backend.Data;
using backend.Models;
using backend.Services;
using backend.Services.Espn;
using backend.Services.Highlights;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace backend.tests;

public sealed class HighlightsPersistenceTests : IAsyncLifetime
{
    private readonly IsolatedPostgreSql database = new IsolatedPostgreSql();
    private readonly AppFactory entryPoint = new();
    private readonly Clock clock = new();
    private WebApplicationFactory<Program> app = null!;
    private JsonObject board = Board();

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 6, 3, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class EspnHandler(Func<string> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response(), Encoding.UTF8, "application/json")
            });
    }

    private static JsonObject Board() => JsonNode.Parse("""
        {"season":{"year":2026,"type":3},"events":[{
          "id":"completed-mlb","date":"2026-10-05T23:00:00Z","name":"Phillies at Braves","shortName":"PHI @ ATL",
          "status":{"period":9,"type":{"name":"STATUS_FINAL","state":"post","completed":true}},
          "competitions":[{"id":"completed-mlb",
            "status":{"period":9,"type":{"name":"STATUS_FINAL","state":"post","completed":true}},
            "competitors":[
              {"homeAway":"home","score":"3","team":{"id":"15","displayName":"Atlanta Braves","abbreviation":"ATL"}},
              {"homeAway":"away","score":"1","team":{"id":"22","displayName":"Philadelphia Phillies","abbreviation":"PHI"}}
            ]}]
        }]}
        """)!.AsObject();

    private WebApplicationFactory<Program> CreateHost() => entryPoint.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
    {
        services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
        services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
        services.AddDbContext<ExcitementDbContext>(options => options.UseNpgsql(database.GetConnectionString()));
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(clock);
        services.AddHttpClient<EspnOddsService>().ConfigurePrimaryHttpMessageHandler(() => new EspnHandler(() => board.ToJsonString()));
    }));

    public async Task InitializeAsync()
    {
        await database.StartAsync();
        app = CreateHost();
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.EnsureCreatedAsync();
    }

    private async Task Collect(string sport = "mlb")
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EspnOddsService>().ScrapeAndStoreOddsAsync(sport, null);
    }

    [Fact]
    public async Task Confirmed_completion_is_observed_without_inventing_an_end_time()
    {
        await Collect();
        using var scope = app.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<GameHighlightsInspectionService>().InspectAsync("completed-mlb");
        Assert.NotNull(result);
        Assert.True(result.IsActuallyCompleted);
        Assert.Equal("final", result.Game.Status);
        Assert.Null(result.Game.EndTime);
        Assert.Equal(new DateTime(2026, 10, 6, 3, 0, 0, DateTimeKind.Utc), result.Game.CompletionConfirmedAt);
        Assert.Equal(new DateTime(2026, 10, 9, 3, 0, 0, DateTimeKind.Utc), result.Game.HighlightsDiscoveryExpiresAt);
    }

    private async Task<GameHighlightsInspection> Inspect()
    {
        using var scope = app.Services.CreateScope();
        return Assert.IsType<GameHighlightsInspection>(await scope.ServiceProvider.GetRequiredService<GameHighlightsInspectionService>().InspectAsync("completed-mlb"));
    }

    [Theory]
    [InlineData("post-false")]
    [InlineData("postponed")]
    [InlineData("suspended")]
    [InlineData("canceled")]
    [InlineData("scheduled")]
    [InlineData("live")]
    [InlineData("missing-flag")]
    [InlineData("missing-competition-status")]
    [InlineData("contradictory-competition")]
    [InlineData("contradictory-description")]
    [InlineData("contradictory-detail")]
    public async Task Stored_final_never_substitutes_for_actual_completion(string variant)
    {
        var ev = board["events"]![0]!;
        var status = ev["status"]!["type"]!;
        var competition = ev["competitions"]![0]!;
        switch (variant)
        {
            case "post-false": status["completed"] = false; break;
            case "postponed": status["name"] = "STATUS_POSTPONED"; break;
            case "suspended": status["name"] = "STATUS_SUSPENDED"; break;
            case "canceled": status["name"] = "STATUS_CANCELED"; break;
            case "scheduled": status["name"] = "STATUS_SCHEDULED"; status["state"] = "pre"; break;
            case "live": status["name"] = "STATUS_IN_PROGRESS"; status["state"] = "in"; break;
            case "missing-flag": status.AsObject().Remove("completed"); break;
            case "missing-competition-status": competition.AsObject().Remove("status"); break;
            case "contradictory-competition": competition["status"]!["type"]!["completed"] = false; break;
            case "contradictory-description": status["description"] = "Postponed"; break;
            case "contradictory-detail": competition["status"]!["detail"] = "Suspended"; break;
        }
        if (variant is "scheduled" or "live")
            ev["date"] = "2099-10-05T23:00:00Z";
        await Collect();
        var result = await Inspect();
        Assert.False(result.IsActuallyCompleted);
        Assert.Null(result.Game.CompletionConfirmedAt);
        Assert.Null(result.Game.HighlightsDiscoveryExpiresAt);
        if (variant is not ("scheduled" or "live")) Assert.Equal("final", result.Game.Status);
    }

    [Fact]
    public async Task Completion_and_original_deadline_survive_refresh_contradiction_and_host_restart()
    {
        await Collect();
        clock.Now = new(2026, 10, 10, 3, 0, 0, TimeSpan.Zero);
        board["events"]![0]!["status"]!["type"]!["completed"] = false;
        await Collect();
        Assert.False((await Inspect()).IsActuallyCompleted);
        await app.DisposeAsync();
        app = CreateHost();
        board = Board();
        await Collect();
        var result = await Inspect();
        Assert.True(result.IsActuallyCompleted);
        Assert.Equal(new DateTime(2026, 10, 6, 3, 0, 0, DateTimeKind.Utc), result.Game.CompletionConfirmedAt);
        Assert.Equal(new DateTime(2026, 10, 9, 3, 0, 0, DateTimeKind.Utc), result.Game.HighlightsDiscoveryExpiresAt);
    }

    [Fact]
    public async Task Malformed_or_incomplete_persisted_evidence_fails_closed()
    {
        await Collect();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            await db.Games.ExecuteUpdateAsync(s => s.SetProperty(g => g.EspnCompletionEvidenceJson, "{\"EventId\":\"completed-mlb\",\"CompetitionId\":\"completed-mlb\"}"));
        }
        Assert.False((await Inspect()).IsActuallyCompleted);
    }

    [Fact]
    public async Task Manual_ownership_suppression_provider_freshness_and_launch_progress_survive_restart()
    {
        await Collect();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            db.GameHighlights.Add(new GameHighlights
            {
                GameId = "completed-mlb", SelectedVideoId = "fixture-video", Ownership = HighlightsOwnership.Manual,
                Suppressed = true, SelectedAt = clock.Now.UtcDateTime, SuppressedAt = clock.Now.UtcDateTime,
                Availability = HighlightsAvailability.Playable, Title = "Fixture recap", ChannelId = "fixture-channel",
                AllowedRegions = ["US", "CA"], MadeForKids = false, ValidatedAt = clock.Now.UtcDateTime,
                ProviderDataRefreshedAt = clock.Now.UtcDateTime,
                ProviderDataExpiresAt = new DateTime(2026, 11, 5, 3, 0, 0, DateTimeKind.Utc),
                SelectionEvidenceJson = "{\"source\":\"synthetic verified contest\"}"
            });
            var game = await db.Games.SingleAsync(g => g.Id == "completed-mlb");
            game.MlbGamePk = 849819;
            game.MlbOfficialDate = new DateOnly(2026, 10, 5);
            game.HighlightsIdentityEvidenceJson = "{\"season\":2026,\"homeTeamId\":144,\"awayTeamId\":143,\"doubleHeader\":\"N\",\"gameNumber\":1,\"rescheduledFromDate\":null}";
            var state = await db.HighlightsOperations.SingleAsync();
            state.LaunchBoundaryAt = clock.Now.UtcDateTime;
            state.CatchupWindowStart = new DateTime(2026, 9, 29, 3, 0, 0, DateTimeKind.Utc);
            state.CatchupCursorGameId = "completed-mlb";
            state.QuotaDay = new DateOnly(2026, 10, 6);
            state.QuotaUnitsUsed = 900;
            state.BackoffUntil = new DateTime(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc);
            await db.SaveChangesAsync();
        }
        await app.DisposeAsync();
        app = CreateHost();
        var inspection = await Inspect();
        Assert.Equal(849819, inspection.Game.MlbGamePk);
        Assert.Equal(new DateOnly(2026, 10, 5), inspection.Game.MlbOfficialDate);
        Assert.Contains("rescheduledFromDate", inspection.Game.HighlightsIdentityEvidenceJson);
        Assert.Equal(HighlightsOwnership.Manual, inspection.Highlights!.Ownership);
        Assert.True(inspection.Highlights.Suppressed);
        Assert.Equal("fixture-video", inspection.Highlights.SelectedVideoId);
        Assert.Equal(["US", "CA"], inspection.Highlights.AllowedRegions!);
        Assert.Equal(new DateTime(2026, 11, 5, 3, 0, 0, DateTimeKind.Utc), inspection.Highlights.ProviderDataExpiresAt);
        using var readScope = app.Services.CreateScope();
        var operations = await readScope.ServiceProvider.GetRequiredService<GameHighlightsInspectionService>().InspectOperationsAsync();
        Assert.Equal(new DateTime(2026, 9, 29, 3, 0, 0, DateTimeKind.Utc), operations.CatchupWindowStart);
        Assert.Equal("completed-mlb", operations.CatchupCursorGameId);
        Assert.Equal(900, operations.QuotaUnitsUsed);
        Assert.Equal(new DateTime(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc), operations.BackoffUntil);
    }

    [Fact]
    public async Task Real_collection_discovery_failure_recovery_and_restart_reach_persisted_detail_without_render_HTTP()
    {
        var providerFailure = true;
        var providerCalls = 0;
        var identityCalls = 0;
        WebApplicationFactory<Program> JourneyHost() => CreateHost().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.Configure<HighlightsWorkerOptions>(o => o.Enabled = true);
            services.Configure<YouTubeHighlightsOptions>(o => { o.Enabled = true; o.ApiKey = "sentinel-journey-no-live"; o.MaxUploadPages = 1; });
            services.AddHttpClient(YouTubeHighlightsClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new JourneyHandler(request =>
            {
                providerCalls++;
                if (providerFailure) return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
                var path = request.RequestUri!.AbsolutePath;
                var body = path.EndsWith("channels")
                    ? """{"items":[{"id":"UCoLrcjPV5PbUrUyXq5mjc_A","contentDetails":{"relatedPlaylists":{"uploads":"fixture-uploads"}}}]}"""
                    : path.EndsWith("playlistItems")
                    ? """{"items":[{"contentDetails":{"videoId":"journey0653"}}]}"""
                    : """{"items":[{"id":"journey0653","snippet":{"channelId":"UCoLrcjPV5PbUrUyXq5mjc_A","title":"Phillies vs. Braves Highlights (10/5/26) | MLB Highlights","description":"Single game recap","publishedAt":"2026-10-06T02:00:00Z","liveBroadcastContent":"none"},"contentDetails":{"duration":"PT8M"},"status":{"privacyStatus":"public","uploadStatus":"processed","embeddable":true,"madeForKids":false}}]}""";
                return new(HttpStatusCode.OK) { Content = new StringContent(body) };
            }));
            services.AddHttpClient(MlbHighlightsIdentityClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new JourneyHandler(request =>
            {
                identityCalls++;
                var body = request.RequestUri!.Host == "statsapi.mlb.com"
                    ? """{"dates":[{"date":"2026-10-05","games":[{"gamePk":991005,"gameDate":"2026-10-05T23:00:00Z","officialDate":"2026-10-05","season":"2026","doubleHeader":"N","gameNumber":1,"status":{"abstractGameState":"Final","detailedState":"Final","codedGameState":"F"},"teams":{"home":{"team":{"id":144}},"away":{"team":{"id":143}}}}]}]}"""
                    : """{"header":{"id":"completed-mlb","season":{"year":2026},"competitions":[{"id":"completed-mlb","date":"2026-10-05T23:00:00Z","status":{"type":{"name":"STATUS_FINAL","state":"post","completed":true}},"competitors":[{"homeAway":"home","team":{"id":"15","abbreviation":"ATL"}},{"homeAway":"away","team":{"id":"22","abbreviation":"PHI"}}]}]}}""";
                return new(HttpStatusCode.OK) { Content = new StringContent(body) };
            }));
        }));
        async Task<HighlightsLifecycleResult> Cycle()
        {
            using var scope = app.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<HighlightsLifecycleService>().RunAsync();
        }
        await app.DisposeAsync();
        app = JourneyHost();
        await Collect();
        var original = (await Inspect()).Game.HighlightsDiscoveryExpiresAt;
        using (var client = app.CreateClient())
        {
            Assert.Contains("Highlights not available yet", await client.GetStringAsync("/game/completed-mlb"));
            Assert.Equal("TemporaryFailure", (await Cycle()).Reason);
            board["events"]![0]!["competitions"]![0]!["competitors"]![0]!["score"] = "4";
            await Collect();
            Assert.Equal(4, (await Inspect()).Game.HomeScore);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/game/completed-mlb")).StatusCode);
            Assert.Equal(1, providerCalls);
            clock.Now = clock.Now.AddMinutes(15);
            providerFailure = false;
            Assert.Equal(1, (await Cycle()).Selected);
            Assert.Equal(0, (await Cycle()).Selected);
        }
        await app.DisposeAsync();
        app = JourneyHost();
        using var restarted = app.CreateClient();
        var before = (providerCalls, identityCalls);
        using var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(await restarted.GetStringAsync("/game/completed-mlb"));
        Assert.Equal("journey0653", document.QuerySelector("nb-game-highlights")!.GetAttribute("data-video-id"));
        Assert.Equal("https://www.youtube.com/watch?v=journey0653", document.QuerySelector("#highlights-watch")!.GetAttribute("href"));
        Assert.Equal("true", document.QuerySelector("[data-live-terminal]")!.GetAttribute("data-live-terminal"));
        Assert.Null(document.QuerySelector("iframe"));
        Assert.DoesNotContain("sentinel-journey-no-live", document.DocumentElement.OuterHtml);
        Assert.Equal(before, (providerCalls, identityCalls));
        var persisted = await Inspect();
        Assert.Equal(original, persisted.Game.HighlightsDiscoveryExpiresAt);
        Assert.Equal(HighlightsOwnership.Automatic, persisted.Highlights!.Ownership);
        Assert.Equal(4, persisted.Game.HomeScore);
    }

    private sealed class JourneyHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }

    [Fact]
    public async Task Forward_migration_preserves_legacy_games_and_reapplies_without_resetting_durable_state()
    {
        await Collect();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        db.Games.AddRange(new[] { ("legacy-live", "mlb", "in_progress"), ("legacy-scheduled", "mlb", "scheduled"), ("legacy-nfl", "nfl", "final") }
            .Select(x => new Game
            {
                Id = x.Item1, Sport = x.Item2, Status = x.Item3, HomeTeam = "Home", AwayTeam = "Away",
                HomeTeamAbbreviation = "H", AwayTeamAbbreviation = "A", ScheduledStart = clock.Now.UtcDateTime,
                HomeScore = 2, AwayScore = 1
            }));
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            DROP TABLE game_highlights;
            DROP TABLE highlights_operations;
            ALTER TABLE games DROP COLUMN espn_completion_evidence_json, DROP COLUMN espn_completion_observed_at,
                DROP COLUMN completion_confirmed_at, DROP COLUMN highlights_discovery_expires_at,
                DROP COLUMN mlb_game_pk, DROP COLUMN mlb_official_date, DROP COLUMN highlights_identity_evidence_json;
            """);
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "030_MlbHighlights.sql"))
            + await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "031_HighlightsLifecycle.sql"));
        await db.Database.ExecuteSqlRawAsync(sql);
        var legacy = await Inspect();
        Assert.Equal("Atlanta Braves", legacy.Game.HomeTeam);
        Assert.Equal(3, legacy.Game.HomeScore);
        Assert.Equal("final", legacy.Game.Status);
        Assert.False(legacy.IsActuallyCompleted);
        Assert.Null(legacy.Game.CompletionConfirmedAt);
        foreach (var id in new[] { "legacy-live", "legacy-scheduled", "legacy-nfl" })
        {
            var other = await scope.ServiceProvider.GetRequiredService<GameHighlightsInspectionService>().InspectAsync(id);
            Assert.NotNull(other);
            Assert.False(other.IsActuallyCompleted);
            Assert.Null(other.Game.HighlightsDiscoveryExpiresAt);
            Assert.Equal(2, other.Game.HomeScore);
        }
        db.GameHighlights.Add(new GameHighlights { GameId = "completed-mlb", Ownership = HighlightsOwnership.Manual, Suppressed = true });
        var operations = await db.HighlightsOperations.SingleAsync();
        operations.LaunchBoundaryAt = clock.Now.UtcDateTime;
        operations.CatchupWindowStart = new DateTime(2026, 9, 29, 3, 0, 0, DateTimeKind.Utc);
        operations.QuotaDay = new DateOnly(2026, 10, 6);
        operations.QuotaUnitsUsed = 17;
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync(sql);
        var retained = await Inspect();
        Assert.True(retained.Highlights!.Suppressed);
        Assert.Equal(HighlightsOwnership.Manual, retained.Highlights.Ownership);
        var retainedOperations = await scope.ServiceProvider.GetRequiredService<GameHighlightsInspectionService>().InspectOperationsAsync();
        Assert.Equal(17, retainedOperations.QuotaUnitsUsed);
        Assert.Equal(clock.Now.UtcDateTime, retainedOperations.LaunchBoundaryAt);
        var duplicate = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO game_highlights(game_id) VALUES ('completed-mlb')"));
        Assert.Equal("23505", duplicate.SqlState);
        var singleton = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO highlights_operations(id) VALUES (2)"));
        Assert.Equal("23514", singleton.SqlState);
        var quota = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE highlights_operations SET quota_units_used = 1001 WHERE id = 1"));
        Assert.Equal("23514", quota.SqlState);
        await Collect();
        var catchupOnly = await Inspect();
        Assert.True(catchupOnly.IsActuallyCompleted);
        Assert.NotNull(catchupOnly.Game.CompletionConfirmedAt);
        Assert.Null(catchupOnly.Game.HighlightsDiscoveryExpiresAt);
    }

    [Fact]
    public async Task Atomic_update_predicates_and_optimistic_concurrency_preserve_operator_decisions()
    {
        await Collect();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var selected = new GameHighlights { GameId = "completed-mlb", Ownership = HighlightsOwnership.Automatic, SelectedVideoId = "first-video" };
        db.GameHighlights.Add(selected);
        await db.SaveChangesAsync();
        using var operatorScope = app.Services.CreateScope();
        var operatorDb = operatorScope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await operatorDb.GameHighlights.Where(h => h.GameId == "completed-mlb").ExecuteUpdateAsync(s => s
            .SetProperty(h => h.Ownership, HighlightsOwnership.Manual)
            .SetProperty(h => h.SelectedVideoId, "operator-video")
            .SetProperty(h => h.Suppressed, true));
        var automaticChanges = await db.GameHighlights
            .Where(h => h.GameId == "completed-mlb" && !h.Suppressed && h.Ownership != HighlightsOwnership.Manual)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.SelectedVideoId, "late-automatic-video"));
        Assert.Equal(0, automaticChanges);
        selected.SelectedVideoId = "stale-automatic-video";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
        var current = await Inspect();
        Assert.Equal("operator-video", current.Highlights!.SelectedVideoId);
        Assert.True(current.Highlights.Suppressed);
        Assert.Equal(HighlightsOwnership.Manual, current.Highlights.Ownership);
    }

    [Fact]
    public async Task Older_completion_observations_cannot_overwrite_newer_explicit_noncompletion()
    {
        await Collect();
        clock.Now = new(2026, 10, 7, 3, 0, 0, TimeSpan.Zero);
        board["events"]![0]!["status"]!["type"]!["completed"] = false;
        await Collect();
        clock.Now = new(2026, 10, 6, 4, 0, 0, TimeSpan.Zero);
        board = Board();
        await Collect();
        var result = await Inspect();
        Assert.False(result.IsActuallyCompleted);
        Assert.Equal(new DateTime(2026, 10, 6, 3, 0, 0, DateTimeKind.Utc), result.Game.CompletionConfirmedAt);
        Assert.Equal(new DateTime(2026, 10, 9, 3, 0, 0, DateTimeKind.Utc), result.Game.HighlightsDiscoveryExpiresAt);
    }

    [Fact]
    public async Task Other_sports_ingestion_remains_unchanged_without_MLB_completion_evidence()
    {
        await Collect("nba");
        var result = await Inspect();
        Assert.Equal("nba", result.Game.Sport);
        Assert.Equal("final", result.Game.Status);
        Assert.False(result.IsActuallyCompleted);
        Assert.Null(result.Game.EspnCompletionEvidenceJson);
        Assert.Null(result.Game.CompletionConfirmedAt);
        Assert.Null(result.Game.HighlightsDiscoveryExpiresAt);
    }

    public async Task DisposeAsync()
    {
        if (app is not null) await app.DisposeAsync();
        await entryPoint.DisposeAsync();
        await database.DisposeAsync();
    }
}
