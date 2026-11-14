using System.Text.Json;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.tests;

public sealed class ProHypeLifecycleRankingTests : IAsyncLifetime
{
    private readonly MoneyPuckQualificationHarness harness = new();
    private Game game = null!;

    public async Task InitializeAsync()
    {
        harness.Handler.RankingMarker = "Monday October 5, 2026 at 2:00 PM Eastern Time";
        await harness.StartAsync();
        using var scope = harness.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "035_ProHypeLifecycle.sql")));
        Assert.Equal(32, await harness.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        game = ProHypeTests.Game();
        game.HomeTeamAbbreviation = "OTT";
        game.AwayTeamAbbreviation = "NSH";
        game.ScheduledStart = harness.Clock.Now.AddDays(6).UtcDateTime;
        var evidence = ProHypeTests.Evidence(game);
        evidence.ObservedAt = harness.Clock.Now.UtcDateTime;
        game.ProEvidenceJson = JsonSerializer.Serialize(evidence);
        var odds = ProHypeTests.Odds(game);
        odds.FetchedAt = harness.Clock.Now.UtcDateTime;
        db.Games.Add(game);
        db.PregameOdds.Add(odds);
        await db.SaveChangesAsync();
    }

    private async Task<ProHypeResolution> Resolve()
    {
        using var scope = harness.App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().ResolveAsync(game.Id);
    }

    [Fact]
    public async Task Successful_unchanged_expired_ranking_collection_retains_original_tuple_without_renewing_raw_eligibility()
    {
        using (var scope = harness.App.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().CaptureAsync();
        var original = await Resolve();
        Assert.Equal(46.690811288126092, original.Total!.Value, 10);
        var components = JsonSerializer.Serialize(original.Components);
        harness.Clock.Now = harness.Clock.Now.AddHours(48);
        Assert.True(game.ScheduledStart > harness.Clock.Now.UtcDateTime);
        Assert.Equal(original.Total, (await Resolve()).Total);
        var requests = harness.Handler.Requests.Count;
        Assert.Equal(0, await harness.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        Assert.Contains(MoneyPuckRankings.RankingsUrl, harness.Handler.Requests.Skip(requests));
        Assert.Null(MoneyPuckRankings.Parse(harness.Handler.RankingCsv, harness.Handler.RankingMarker, harness.Clock.Now.UtcDateTime));
        using (var scope = harness.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var rows = await db.TeamRatings.ToArrayAsync();
            foreach (var row in rows.Where(r => r.Source == MoneyPuckRankings.Source))
            {
                Assert.Equal(harness.Clock.Now.UtcDateTime, row.FetchedAt);
                Assert.Equal(2027, row.SourceSeason);
                Assert.Equal(new DateTime(2026, 10, 5, 18, 0, 0, DateTimeKind.Utc), row.RankPublishedAt);
            }
            var raw = scope.ServiceProvider.GetRequiredService<HypeScoreService>().EvaluateFromEvidence(game,
                rows, await db.PregameOdds.SingleAsync(), [], harness.Clock.Now.UtcDateTime);
            Assert.False(raw.Components.QualityAvailable);
            Assert.True(double.IsNaN(scope.ServiceProvider.GetRequiredService<HypeScoreService>().CalculateFinalScore(game.Sport, raw.Components)));
            var rankingObservations = raw.Assessment.Observations.Where(r => r.Kind == backend.Scoring.ProEvidenceKind.Ranking).ToArray();
            Assert.Equal(2, rankingObservations.Length);
            Assert.All(rankingObservations, r => Assert.Equal(backend.Scoring.ProEvidenceState.Gap, r.State));
        }
        var retained = await Resolve();
        Assert.Equal(original.Total, retained.Total);
        Assert.Equal(components, JsonSerializer.Serialize(retained.Components));
        Assert.Equal(original.EvaluatedAt, retained.EvaluatedAt);
        await harness.RestartApplicationAsync();
        retained = await Resolve();
        Assert.Equal(original.Total, retained.Total);
        Assert.Equal(components, JsonSerializer.Serialize(retained.Components));
        Assert.Equal(original.EvaluatedAt, retained.EvaluatedAt);
        var html = await harness.Client.GetStringAsync("/game/" + game.Id);
        Assert.Contains("Hype Score", html);
        Assert.Contains("MoneyPuck", html);
        ProHypeOutputAssertions.NoDiagnostics(html);
        var resolutionJson = JsonSerializer.Serialize(retained);
        using var document = JsonDocument.Parse(resolutionJson);
        Assert.Equal(new[] { "Total" }, document.RootElement.EnumerateObject().Select(p => p.Name));
        harness.Clock.Now = harness.Clock.Now.AddHours(24);
        Assert.Equal(0, await harness.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        retained = await Resolve();
        Assert.Equal(original.Total, retained.Total);
        Assert.Equal(components, JsonSerializer.Serialize(retained.Components));
        Assert.Equal(original.EvaluatedAt, retained.EvaluatedAt);
        Assert.Null(MoneyPuckRankings.Parse(harness.Handler.RankingCsv, harness.Handler.RankingMarker, harness.Clock.Now.UtcDateTime));
    }

    [Theory]
    [InlineData("malformed_csv", false)]
    [InlineData("malformed_csv", true)]
    [InlineData("missing_score", false)]
    [InlineData("missing_score", true)]
    [InlineData("missing_team", false)]
    [InlineData("missing_team", true)]
    [InlineData("nonfinite", false)]
    [InlineData("nonfinite", true)]
    [InlineData("duplicate", false)]
    [InlineData("duplicate", true)]
    [InlineData("unsupported", false)]
    [InlineData("unsupported", true)]
    [InlineData("future", false)]
    [InlineData("future", true)]
    [InlineData("missing_marker", false)]
    [InlineData("missing_marker", true)]
    [InlineData("malformed_marker", false)]
    [InlineData("malformed_marker", true)]
    [InlineData("correction", false)]
    [InlineData("correction", true)]
    public async Task Hard_ranking_fault_at_expired_boundary_cannot_restore_original_preview_even_if_overwritten_before_read(string fault, bool restoreBeforeRead)
    {
        using (var scope = harness.App.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().CaptureAsync();
        var original = await Resolve();
        Assert.Equal(46.690811288126092, original.Total!.Value, 10);
        harness.Clock.Now = harness.Clock.Now.AddHours(48);
        Assert.Equal(original.Total, (await Resolve()).Total);
        var csv = harness.Handler.RankingCsv;
        var marker = harness.Handler.RankingMarker;
        harness.Handler.RankingCsv = fault switch
        {
            "malformed_csv" => "not a ranking CSV",
            "missing_score" => csv.Replace("NSH,0.5", "NSH,"),
            "missing_team" => csv.Replace("NSH,0.5\n", ""),
            "nonfinite" => csv.Replace("NSH,0.5", "NSH,NaN"),
            "duplicate" => csv.Replace("NSH,0.5", "OTT,0.5"),
            "correction" => csv.Replace("NSH,0.5\nANA,0.5", "ANA,0.5\nNSH,0.5"),
            _ => csv
        };
        harness.Handler.RankingMarker = fault switch
        {
            "unsupported" => "Saturday October 5, 2024 at 2:00 PM Eastern Time",
            "future" => "Wednesday October 14, 2026 at 2:00 PM Eastern Time",
            "missing_marker" => "",
            "malformed_marker" => "not a publication date",
            _ => marker
        };
        if (fault == "correction") Assert.NotEqual(csv, harness.Handler.RankingCsv);
        Assert.Equal(0, await harness.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        if (!restoreBeforeRead) Assert.Null((await Resolve()).Total);
        harness.Handler.RankingCsv = csv;
        harness.Handler.RankingMarker = marker;
        harness.Clock.Now = harness.Clock.Now.AddHours(24);
        Assert.Equal(0, await harness.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        Assert.Null((await Resolve()).Total);
        var requests = harness.Handler.Requests.Count;
        await harness.RestartApplicationAsync();
        using (var scope = harness.App.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().CaptureAsync();
        Assert.Null((await Resolve()).Total);
        var html = await harness.Client.GetStringAsync("/game/" + game.Id);
        Assert.Contains("Hype unavailable", html);
        ProHypeOutputAssertions.NoDiagnostics(html);
        Assert.Equal(requests, harness.Handler.Requests.Count);
    }

    [Fact]
    public async Task Same_marker_correction_to_other_teams_does_not_withdraw_the_actual_ranking_support()
    {
        using (var scope = harness.App.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().CaptureAsync();
        var original = await Resolve();
        Assert.Equal(46.690811288126092, original.Total!.Value, 10);
        harness.Clock.Now = harness.Clock.Now.AddHours(48);
        harness.Handler.RankingCsv = harness.Handler.RankingCsv.Replace("ANA,0.5\nBOS,0.5", "BOS,0.5\nANA,0.5");
        Assert.Equal(0, await harness.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        await harness.RestartApplicationAsync();
        var retained = await Resolve();
        Assert.Equal(original.Total, retained.Total);
        Assert.Equal(JsonSerializer.Serialize(original.Components), JsonSerializer.Serialize(retained.Components));
        Assert.Equal(original.EvaluatedAt, retained.EvaluatedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_ranking_support_without_value_retains_unchanged_expiry_and_rejects_same_marker_correction(bool correction)
    {
        using (var scope = harness.App.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().CaptureAsync();
        var original = await Resolve();
        Assert.Equal(46.690811288126092, original.Total!.Value, 10);
        using (var scope = harness.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var state = await db.ProHypeSnapshots.SingleAsync();
            var saved = System.Text.Json.Nodes.JsonNode.Parse(state.SnapshotJson!)!;
            foreach (var reference in saved["SupportingEvidence"]!.AsArray())
                reference!.AsObject().Remove("PowerRank");
            state.SnapshotJson = saved.ToJsonString();
            await db.SaveChangesAsync();
        }
        harness.Clock.Now = harness.Clock.Now.AddHours(48);
        var csv = harness.Handler.RankingCsv;
        if (correction) harness.Handler.RankingCsv = csv.Replace("NSH,0.5\nANA,0.5", "ANA,0.5\nNSH,0.5");
        Assert.Equal(0, await harness.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        var retained = await Resolve();
        if (correction) Assert.Null(retained.Total);
        else
        {
            Assert.Equal(original.Total, retained.Total);
            Assert.Equal(original.EvaluatedAt, retained.EvaluatedAt);
            Assert.Equal(JsonSerializer.Serialize(original.Components), JsonSerializer.Serialize(retained.Components));
        }
        harness.Handler.RankingCsv = csv;
        harness.Clock.Now = harness.Clock.Now.AddHours(24);
        Assert.Equal(0, await harness.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        await harness.RestartApplicationAsync();
        retained = await Resolve();
        if (correction) Assert.Null(retained.Total);
        else
        {
            Assert.Equal(original.Total, retained.Total);
            Assert.Equal(original.EvaluatedAt, retained.EvaluatedAt);
            Assert.Equal(JsonSerializer.Serialize(original.Components), JsonSerializer.Serialize(retained.Components));
        }
    }

    public async Task DisposeAsync() => await harness.DisposeAsync();
}
