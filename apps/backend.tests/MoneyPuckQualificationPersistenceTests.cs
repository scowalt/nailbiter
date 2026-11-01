using System.Data.Common;
using System.Net;
using System.Text.Json.Nodes;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.tests;

public sealed class MoneyPuckQualificationPersistenceTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Prior_envelopes_without_rejected_identity_metadata_retain_their_known_raw_rejection_on_next_invalidation(int version)
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        h.Clock.Now = h.Clock.Now.AddHours(24);
        var rejected = h.Clock.Now.AddHours(72);
        h.Handler.Marker = rejected.ToString("O");
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        using (var scope = h.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            foreach (var row in await db.TeamRatings.Where(r => r.Source == MoneyPuckQualificationHarness.Source).ToListAsync())
            {
                var json = JsonNode.Parse(row.PlayoffEvidenceJson!)!;
                json["Version"] = version;
                json.AsObject().Remove("MoneyPuckRejectedPublications");
                if (version == 3) json.AsObject().Remove("PublicationBoundary");
                row.PlayoffEvidenceJson = json.ToJsonString();
            }
            await db.SaveChangesAsync();
        }
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Marker = "";
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        h.Clock.Now = rejected.AddMinutes(5);
        h.Handler.Marker = rejected.ToString("O");
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        foreach (var team in MoneyPuckQualificationHarness.Teams) Assert.Null((await h.SelectAsync(team)).Probability);
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(.2518, (await h.SelectAsync()).Probability.Value);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("rejected")]
    [InlineData("future")]
    public async Task Prior_collector_v3_envelopes_preserve_only_clock_valid_boundaries(string state)
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        var prior = PlayoffModelEvidence.Parse((await h.RowsAsync())[0].PlayoffEvidenceJson)!.PublishedAt!.Value;
        if (state != "accepted")
        {
            h.Clock.Now = h.Clock.Now.AddHours(24);
            h.Handler.Marker = (state == "future" ? h.Clock.Now.AddYears(5) : h.Clock.Now.AddMinutes(-5)).ToString("O");
            if (state == "rejected") h.Handler.Csv = h.Handler.Csv.Replace("NSH,ALL,0.2518", "NSH,ALL,NaN");
            Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        }
        var lastPublished = PlayoffModelEvidence.Parse((await h.RowsAsync())[0].PlayoffEvidenceJson)!.PublishedAt!.Value;
        using (var scope = h.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            foreach (var row in await db.TeamRatings.Where(r => r.Source == MoneyPuckQualificationHarness.Source).ToListAsync())
            {
                var json = JsonNode.Parse(row.PlayoffEvidenceJson!)!;
                json["Version"] = 3;
                json.AsObject().Remove("PublicationBoundary");
                row.PlayoffEvidenceJson = json.ToJsonString();
            }
            await db.SaveChangesAsync();
        }
        if (state == "accepted") Assert.Equal(.2518, (await h.SelectAsync()).Probability.Value);
        else Assert.Null((await h.SelectAsync()).Probability);
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Csv = MoneyPuckQualificationHandler.Fixture("simulations.csv");
        h.Handler.Marker = (state == "accepted" ? prior.AddMinutes(-1)
            : state == "rejected" ? lastPublished : h.Clock.Now.AddMinutes(-5).UtcDateTime).ToString("O");
        Assert.Equal(state == "future" ? 32 : 0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(state == "future", (await h.SelectAsync()).Probability is not null);
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(.2518, (await h.SelectAsync()).Probability.Value);
    }

    private sealed class CancelCommit(CancellationTokenSource cts) : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!Enabled) return ValueTask.FromResult(result);
            cts.Cancel();
            return ValueTask.FromException<InterceptionResult>(new OperationCanceledException(cts.Token));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_late_database_failure_leaves_no_partial_accepted_or_rejected_batch(bool reject)
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        var original = await h.RowsAsync();
        using var scope = h.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_qualification_update() RETURNS trigger AS $$
            BEGIN
                IF NEW.source = 'moneypuck_qualification' AND NEW.team_abbreviation = 'WPG' THEN
                    RAISE EXCEPTION 'synthetic late qualification failure';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER fail_qualification_update_trigger BEFORE UPDATE ON team_ratings FOR EACH ROW EXECUTE FUNCTION fail_qualification_update();
            """);
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        h.Handler.Csv = h.Handler.Csv.Replace("NSH,ALL,0.2518", reject ? "NSH,ALL,NaN" : "NSH,ALL,0.3");
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(original.Select(r => r.PlayoffEvidenceJson), (await h.RowsAsync()).Select(r => r.PlayoffEvidenceJson));
        Assert.Equal(original.Select(r => r.FetchedAt), (await h.RowsAsync()).Select(r => r.FetchedAt));
        Assert.Equal(.2518, (await h.SelectAsync()).Probability.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_at_commit_rolls_back_whole_accepted_or_rejected_batch(bool reject)
    {
        using var cts = new CancellationTokenSource();
        var interceptor = new CancelCommit(cts);
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync(options => options.AddInterceptors(interceptor));
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        var original = await h.RowsAsync();
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        h.Handler.Csv = h.Handler.Csv.Replace("NSH,ALL,0.2518", reject ? "NSH,ALL,NaN" : "NSH,ALL,0.3");
        interceptor.Enabled = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Service.RefreshNhlRatingsAsync(cts.Token));
        Assert.Equal(original.Select(r => r.PlayoffEvidenceJson), (await h.RowsAsync()).Select(r => r.PlayoffEvidenceJson));
        Assert.Equal(original.Select(r => r.FetchedAt), (await h.RowsAsync()).Select(r => r.FetchedAt));
        Assert.Equal(.2518, (await h.SelectAsync()).Probability.Value);
    }

    [Fact]
    public async Task Caller_cancellation_during_HTTP_retains_observations_and_counts_as_an_attempt()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        var original = await h.RowsAsync();
        h.Clock.Now = h.Clock.Now.AddHours(24);
        using var cts = new CancellationTokenSource();
        h.Handler.Override = (request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri == MoneyPuckQualificationHandler.CsvUrl)
            {
                cts.Cancel();
                return Task.FromCanceled<HttpResponseMessage?>(cts.Token);
            }
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Service.RefreshNhlRatingsAsync(cts.Token));
        Assert.Equal(12, h.Handler.Requests.Count);
        Assert.Equal(original.Select(r => r.PlayoffEvidenceJson), (await h.RowsAsync()).Select(r => r.PlayoffEvidenceJson));
        h.Handler.Override = null;
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(12, h.Handler.Requests.Count);
    }

    [Fact]
    public async Task Qualification_and_actual_ranking_refreshes_have_independent_values_and_clocks()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        var ranks = h.App.Services.GetRequiredService<MoneyPuckService>();
        Assert.Equal(32, await ranks.RefreshNhlRatingsAsync(default));
        var rankRows = await h.RowsAsync("moneypuck");
        var game = ProHypeTests.Game();
        game.Id = "synthetic-rank-isolation";
        game.ScheduledStart = h.Clock.Now.AddDays(10).UtcDateTime;
        async Task<double> Quality()
        {
            var evidence = ProHypeTests.Evidence(game);
            evidence.ObservedAt = h.Clock.Now.UtcDateTime;
            game.ProEvidenceJson = System.Text.Json.JsonSerializer.Serialize(evidence);
            using var scope = h.App.Services.CreateScope();
            var metrics = await scope.ServiceProvider.GetRequiredService<HypeScoreService>().GetHypeComponentsAsync(game, null);
            Assert.True(metrics.QualityAvailable);
            return metrics.QualityScore;
        }
        var originalQuality = await Quality();
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        var forecasts = await h.RowsAsync();
        Assert.Equal(originalQuality, await Quality());
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.RankingMarker = "Monday October 12, 2026 at 1:00 PM Eastern Time";
        Assert.Equal(32, await ranks.RefreshNhlRatingsAsync(default));
        var advancedRanks = await h.RowsAsync("moneypuck");
        Assert.Equal(rankRows.Select(r => r.PowerRank), advancedRanks.Select(r => r.PowerRank));
        Assert.All(advancedRanks, row => Assert.Equal(h.Clock.Now.UtcDateTime, row.FetchedAt));
        Assert.All(advancedRanks, row => Assert.Equal(new DateTime(2026, 10, 12, 17, 0, 0, DateTimeKind.Utc), row.RankPublishedAt));
        Assert.Equal(forecasts.Select(r => r.PlayoffEvidenceJson), (await h.RowsAsync()).Select(r => r.PlayoffEvidenceJson));
        Assert.Equal(forecasts.Select(r => r.FetchedAt), (await h.RowsAsync()).Select(r => r.FetchedAt));
        var due = h.Clock.Now.AddHours(24);
        h.Clock.Now = new DateTimeOffset(PlayoffModelEvidence.Parse(forecasts[0].PlayoffEvidenceJson)!.PublishedAt!.Value.AddHours(36));
        Assert.Null((await h.SelectAsync()).Probability);
        Assert.Equal(originalQuality, await Quality());
        h.Clock.Now = due;
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        h.Handler.Csv = "invalid successful snapshot";
        var markers = 0;
        h.Handler.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsoluteUri == MoneyPuckRankings.UpdatedUrl && ++markers == 2
            ? new(HttpStatusCode.OK) { Content = new StringContent("Monday October 12, 2026 at 2:00 PM Eastern Time") } : null);
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(advancedRanks.Select(r => (r.PowerRank, r.RankPublishedAt, r.FetchedAt)), (await h.RowsAsync("moneypuck")).Select(r => (r.PowerRank, r.RankPublishedAt, r.FetchedAt)));
        var rejected = await h.RowsAsync();
        Assert.Equal(originalQuality, await Quality());
        Assert.Equal(0, await ranks.RefreshNhlRatingsAsync(default));
        Assert.Equal(originalQuality, await Quality());
        Assert.Equal(rejected.Select(r => r.PlayoffEvidenceJson), (await h.RowsAsync()).Select(r => r.PlayoffEvidenceJson));
        Assert.Equal(rejected.Select(r => r.FetchedAt), (await h.RowsAsync()).Select(r => r.FetchedAt));
        Assert.Null((await h.SelectAsync()).Probability);
    }

}
