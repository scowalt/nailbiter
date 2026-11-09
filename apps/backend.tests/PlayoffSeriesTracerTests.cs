using backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.tests;

public sealed class PlayoffSeriesTracerTests : IAsyncLifetime
{
    private readonly PlayoffSeriesFixture fixture = new();
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();

    [Fact]
    public async Task Synthetic_standing_coexists_with_the_independently_specified_hype_score()
    {
        await fixture.CollectAsync();
        using (var scope = fixture.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var game = await db.Games.SingleAsync();
            db.TeamRatings.AddRange(ProHypeTests.Ranks(game, 4, 8));
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<backend.Services.ProCompetitionEvidenceService>().RefreshAsync(default);
            await scope.ServiceProvider.GetRequiredService<backend.Services.PregameOddsService>().RefreshPregameOddsForSportAsync("mlb", null);
            await scope.ServiceProvider.GetRequiredService<backend.Services.IProHypeLifecycle>().CaptureAsync();
        }
        backend.Services.ProHypeResolution captured;
        using (var scope = fixture.Host.Services.CreateScope())
        {
            captured = await scope.ServiceProvider.GetRequiredService<backend.Services.IProHypeLifecycle>().ResolveAsync("401907973");
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            for (var replay = 0; replay < 2; replay++)
                foreach (var migration in new[] { "034_PlayoffSeriesObservation.sql", "035_ProHypeLifecycle.sql" })
                    await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", migration)));
        }
        var requests = fixture.Provider.Requests;
        using var page = await fixture.HomeAsync();
        var card = page.QuerySelector("a[href='/game/401907973']")!;
        Assert.Contains("Series tied 1–1", card.TextContent);
        Assert.Contains("NLWC · Game 3 · Best of 3", card.TextContent);
        Assert.Contains("Hype81", card.QuerySelector("#excitement-401907973")!.TextContent);
        Assert.Equal("", card.QuerySelector("#away-score-401907973")!.TextContent);
        Assert.Equal("", card.QuerySelector("#home-score-401907973")!.TextContent);
        Assert.Equal(requests, fixture.Provider.Requests);
        using var restoredScope = fixture.Host.Services.CreateScope();
        var restored = await restoredScope.ServiceProvider.GetRequiredService<backend.Services.IProHypeLifecycle>().ResolveAsync("401907973");
        Assert.Equal(captured.Total, restored.Total);
        Assert.Equal(captured.EvaluatedAt, restored.EvaluatedAt);
    }

    [Fact]
    public async Task Explicit_opening_zeroes_are_visible_but_missing_or_fractional_wins_are_unavailable()
    {
        var competition = fixture.Provider.Board["events"]![0]!["competitions"]![0]!;
        competition["notes"]![0]!["headline"] = "NLWC - Game 1";
        competition["series"]!["competitors"]![0]!["wins"] = 0;
        competition["series"]!["competitors"]![1]!["wins"] = 0;
        await fixture.CollectAsync();
        using (var page = await fixture.HomeAsync()) Assert.Contains("Series tied 0–0", page.QuerySelector("a[href='/game/401907973']")!.TextContent);
        foreach (var malformed in new[] { false, true })
        {
            fixture.Host.Clock.Advance(TimeSpan.FromMinutes(1));
            if (malformed) competition["series"]!["competitors"]![0]!["wins"] = 0.5;
            else competition["series"]!["competitors"]![0]!.AsObject().Remove("wins");
            await fixture.CollectAsync();
            using var page = await fixture.HomeAsync();
            var card = page.QuerySelector("a[href='/game/401907973']")!;
            Assert.NotNull(card);
            Assert.DoesNotContain("Series tied", card.TextContent);
            Assert.Equal("", card.QuerySelector("#home-score-401907973")!.TextContent);
        }
    }

    [Fact]
    public async Task Transport_failure_preserves_standing_and_does_not_renew_observation_barrier()
    {
        await fixture.CollectAsync();
        fixture.Host.Clock.Advance(TimeSpan.FromMinutes(1));
        var competition = fixture.Provider.Board["events"]![0]!["competitions"]![0]!;
        competition["series"]!["competitors"]![0]!["wins"] = 2;
        competition["series"]!["totalCompetitions"] = 5;
        competition["notes"]![0]!["headline"] = "NLDS - Game 4";
        var held = fixture.Provider.HoldNextBoard();
        var older = fixture.CollectAsync();
        try
        {
            await held.WaitUntilRequestedAsync();
            fixture.Host.Clock.Advance(TimeSpan.FromMinutes(1));
            fixture.Provider.TransportFailure = true;
            await fixture.CollectAsync();
            using var page = await fixture.HomeAsync();
            Assert.Contains("Series tied 1–1", page.QuerySelector("a[href='/game/401907973']")!.TextContent);
        }
        finally { held.Release(); await older; }
        using var refreshed = await fixture.HomeAsync();
        Assert.Contains("ATL leads 2–1", refreshed.QuerySelector("a[href='/game/401907973']")!.TextContent);
    }

    [Fact]
    public async Task Successful_invalidation_keeps_a_barrier_against_older_valid_observations()
    {
        await fixture.CollectAsync();
        fixture.Host.Clock.Advance(TimeSpan.FromMinutes(1));
        var held = fixture.Provider.HoldNextBoard();
        var older = fixture.CollectAsync();
        try
        {
            await held.WaitUntilRequestedAsync();
            fixture.Host.Clock.Advance(TimeSpan.FromMinutes(1));
            fixture.Provider.Board["events"]![0]!["competitions"]![0]!.AsObject().Remove("series");
            await fixture.CollectAsync();
        }
        finally { held.Release(); await older; }
        using var page = await fixture.HomeAsync();
        Assert.DoesNotContain("Series tied", page.QuerySelector("a[href='/game/401907973']")!.TextContent);
    }

    [Fact]
    public async Task Nullable_migration_is_repeatable_and_keeps_legacy_games_unavailable()
    {
        await fixture.CollectAsync();
        using var scope = fixture.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE games DROP COLUMN series_observation_json");
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "034_PlayoffSeriesObservation.sql"))
            + "\n" + await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "035_ProHypeLifecycle.sql"));
        await db.Database.ExecuteSqlRawAsync(sql);
        await db.Database.ExecuteSqlRawAsync(sql);
        Assert.Null((await db.Games.AsNoTracking().SingleAsync()).SeriesObservationJson);
        using var page = await fixture.HomeAsync();
        var card = page.QuerySelector("a[href='/game/401907973']")!;
        Assert.NotNull(card);
        Assert.DoesNotContain("Series tied", card.TextContent);
        Assert.Contains("Hype unavailable", card.TextContent);
    }

    [Fact]
    public async Task Successful_observation_without_competition_revokes_the_previous_standing()
    {
        await fixture.CollectAsync();
        using (var page = await fixture.HomeAsync()) Assert.Contains("Series tied 1–1", page.QuerySelector("a[href='/game/401907973']")!.TextContent);
        fixture.Host.Clock.Advance(TimeSpan.FromMinutes(1));
        fixture.Provider.Board["events"]![0]!["competitions"] = new System.Text.Json.Nodes.JsonArray();
        await fixture.CollectAsync();
        using var refreshed = await fixture.HomeAsync();
        var card = refreshed.QuerySelector("a[href='/game/401907973']")!;
        Assert.NotNull(card);
        Assert.DoesNotContain("Series tied", card.TextContent);
        Assert.DoesNotContain("Best of", card.TextContent);
        Assert.Contains("Hype unavailable", card.TextContent);
    }

    [Fact]
    public async Task Synthetic_scheduled_standing_survives_collection_and_new_home_requests_without_odds()
    {
        await fixture.CollectAsync();
        var requests = fixture.Provider.Requests;
        for (var load = 0; load < 2; load++)
        {
            using var page = await fixture.HomeAsync();
            var card = page.QuerySelector("a[href='/game/401907973']")!;
            Assert.NotNull(card);
            Assert.Contains("Series tied 1–1", card.TextContent);
            Assert.Contains("NLWC · Game 3 · Best of 3", card.TextContent);
            Assert.Contains("Hype unavailable", card.TextContent);
            Assert.Equal("", card.QuerySelector("#home-score-401907973")!.TextContent);
            Assert.Equal("", card.QuerySelector("#away-score-401907973")!.TextContent);
            Assert.DoesNotContain("prototype", card.TextContent, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(requests, fixture.Provider.Requests);
    }
}
