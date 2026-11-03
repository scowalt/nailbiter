using System.Net;
using backend.Data;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace backend.tests;

public sealed class MoneyPuckDailyRefreshTests
{
    [Fact]
    public async Task Independent_backend_instances_share_one_admission_and_the_loser_can_collect_next_day()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        await using var peer = h.CreatePeerApplication();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        h.Handler.Override = async (_, ct) =>
        {
            if (first) { first = false; entered.SetResult(); await release.Task.WaitAsync(ct); }
            return null;
        };
        var collecting = h.Service.RefreshNhlRatingsAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var ranks = peer.Services.GetRequiredService<MoneyPuckService>();
            Assert.Equal(0, await ranks.RefreshNhlRatingsAsync(default));
            Assert.Single(h.Handler.Requests);
            release.SetResult();
            Assert.Equal(32, await collecting);
            Assert.Equal(9, h.Handler.Requests.Count);
            h.Clock.Now = h.Clock.Now.AddHours(24).AddTicks(-1);
            Assert.Equal(0, await ranks.RefreshNhlRatingsAsync(default));
            Assert.Equal(9, h.Handler.Requests.Count);
            h.Clock.Now = h.Clock.Now.AddTicks(1);
            h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
            Assert.Equal(32, await ranks.RefreshNhlRatingsAsync(default));
            Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
            Assert.Equal(18, h.Handler.Requests.Count);
        }
        finally { release.TrySetResult(); await collecting; }
    }

    [Fact]
    public async Task Rankings_only_collection_has_the_same_restart_safe_daily_budget_without_forecast_requests()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync(enabled: false);
        var ranks = h.App.Services.GetRequiredService<MoneyPuckService>();
        Assert.Equal(32, await ranks.RefreshNhlRatingsAsync(default));
        Assert.Equal(3, h.Handler.Requests.Count);
        Assert.Empty(await h.RowsAsync());
        Assert.DoesNotContain(h.Handler.Requests, url => url == MoneyPuckQualificationHandler.PageUrl || url == MoneyPuckQualificationHandler.CsvUrl);
        h.Clock.Now = h.Clock.Now.AddHours(6);
        Assert.Equal(0, await ranks.RefreshNhlRatingsAsync(default));
        await h.RestartApplicationAsync();
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(0, await h.App.Services.GetRequiredService<MoneyPuckService>().RefreshNhlRatingsAsync(default));
        Assert.Equal(3, h.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(1, 401, "")]
    [InlineData(2, 403, "")]
    [InlineData(3, 429, "")]
    [InlineData(2, 200, "Please verify you are human: CAPTCHA")]
    public async Task Ranking_denial_stops_the_cycle_and_blocks_both_inputs_until_backoff_expires_across_restart(int stopAt, int status, string body)
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync(enabled: false);
        var ranks = h.App.Services.GetRequiredService<MoneyPuckService>();
        Assert.Equal(32, await ranks.RefreshNhlRatingsAsync(default));
        var previous = await h.RowsAsync("moneypuck");
        h.Clock.Now = h.Clock.Now.AddHours(24);
        var calls = 0;
        h.Handler.Override = (_, _) => Task.FromResult<HttpResponseMessage?>(++calls == stopAt
            ? new((HttpStatusCode)status) { Content = new StringContent(body) } : null);
        Assert.Equal(0, await ranks.RefreshNhlRatingsAsync(default));
        Assert.Equal(stopAt, calls);
        Assert.Equal(previous.Select(row => (row.PowerRank, row.RankPublishedAt, row.FetchedAt)), (await h.RowsAsync("moneypuck")).Select(row => (row.PowerRank, row.RankPublishedAt, row.FetchedAt)));
        h.Handler.Override = null;
        await h.RestartApplicationAsync();
        h.Clock.Now = h.Clock.Now.AddHours(24).AddTicks(-1);
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(0, await h.App.Services.GetRequiredService<MoneyPuckService>().RefreshNhlRatingsAsync(default));
        Assert.Equal(3 + stopAt, h.Handler.Requests.Count);
        Assert.Empty(await h.RowsAsync());
        h.Clock.Now = h.Clock.Now.AddTicks(1);
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(12 + stopAt, h.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Bounded_invalid_ranking_body_still_finishes_its_own_publication_bracket(bool invalidEncoding, bool race)
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync(enabled: false);
        var ranks = h.App.Services.GetRequiredService<MoneyPuckService>();
        Assert.Equal(32, await ranks.RefreshNhlRatingsAsync(default));
        var previous = await h.RowsAsync("moneypuck");
        h.Clock.Now = h.Clock.Now.AddHours(24);
        var markers = 0;
        h.Handler.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsoluteUri switch
        {
            MoneyPuckRankings.RankingsUrl => new(HttpStatusCode.OK) { Content = invalidEncoding ? new ByteArrayContent([0xc3, 0x28]) : new StringContent(new string('x', 2097153)) },
            MoneyPuckRankings.UpdatedUrl when ++markers == 2 && race => new(HttpStatusCode.OK) { Content = new StringContent("Monday October 12, 2026 at 1:00 PM Eastern Time") },
            _ => null
        });
        Assert.Equal(0, await ranks.RefreshNhlRatingsAsync(default));
        Assert.Equal(6, h.Handler.Requests.Count);
        Assert.Equal(2, markers);
        var latest = await h.RowsAsync("moneypuck");
        if (race) Assert.Equal(previous.Select(row => (row.PowerRank, row.RankPublishedAt, row.FetchedAt)), latest.Select(row => (row.PowerRank, row.RankPublishedAt, row.FetchedAt)));
        else Assert.All(latest, row => { Assert.Null(row.PowerRank); Assert.Null(row.RankPublishedAt); Assert.Equal(h.Clock.Now.UtcDateTime, row.FetchedAt); });
        Assert.Empty(await h.RowsAsync());
    }

    [Fact]
    public async Task Caller_cancelled_attempt_remains_charged_after_restart()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        using var cancellation = new CancellationTokenSource();
        h.Handler.Override = async (_, ct) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Service.RefreshNhlRatingsAsync(cancellation.Token));
        Assert.Single(h.Handler.Requests);
        h.Handler.Override = null;
        await h.RestartApplicationAsync();
        h.Clock.Now = h.Clock.Now.AddHours(24).AddTicks(-1);
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Single(h.Handler.Requests);
        h.Clock.Now = h.Clock.Now.AddTicks(1);
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(10, h.Handler.Requests.Count);
    }

    [Fact]
    public async Task Missing_admission_storage_fails_closed_and_repeatable_migration_does_not_reset_the_budget()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        using var scope = h.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE moneypuck_refresh_state");
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(0, await h.App.Services.GetRequiredService<MoneyPuckService>().RefreshNhlRatingsAsync(default));
        Assert.Empty(h.Handler.Requests);
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "033_MoneyPuckDailyRefresh.sql"));
        await db.Database.ExecuteSqlRawAsync(sql);
        await db.Database.ExecuteSqlRawAsync(sql);
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Empty(h.Handler.Requests);
        var expiry = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "036_MoneyPuckRefusalExpiry.sql"));
        await db.Database.ExecuteSqlRawAsync(expiry);
        await db.Database.ExecuteSqlRawAsync(expiry);
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        await db.Database.ExecuteSqlRawAsync(sql);
        await db.Database.ExecuteSqlRawAsync(expiry);
        await h.RestartApplicationAsync();
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(9, h.Handler.Requests.Count);
        Assert.Equal(.2518, (await h.SelectAsync()).Probability.Value);
    }

    [Fact]
    public async Task Failed_finalization_stays_blocked_across_restart_instead_of_losing_denial()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        using var scope = h.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_moneypuck_finalize() RETURNS trigger AS $$
            BEGIN
                IF NOT NEW.active THEN RAISE EXCEPTION 'synthetic finalization failure'; END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER fail_moneypuck_finalize_trigger BEFORE UPDATE ON moneypuck_refresh_state FOR EACH ROW EXECUTE FUNCTION fail_moneypuck_finalize();
            """);
        h.Handler.Override = (_, _) => Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.Forbidden));
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Single(h.Handler.Requests);
        h.Handler.Override = null;
        await h.RestartApplicationAsync();
        h.Clock.Now = h.Clock.Now.AddDays(2);
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(0, await h.App.Services.GetRequiredService<MoneyPuckService>().RefreshNhlRatingsAsync(default));
        Assert.Single(h.Handler.Requests);
    }

    [Fact]
    public async Task Actual_daily_worker_waits_for_the_persisted_boundary_and_collects_both_inputs()
    {
        await using var h = new MoneyPuckQualificationHarness();
        var time = new FakeTimeProvider(h.Clock.Now);
        await h.StartAsync(configureServices: services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(time); });
        using var worker = ActivatorUtilities.CreateInstance<MoneyPuckRefreshBackgroundService>(h.App.Services);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            async Task<bool> Complete()
            {
                using var scope = h.App.Services.CreateScope();
                var state = await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().MoneyPuckRefreshOperations.AsNoTracking().SingleOrDefaultAsync();
                return state is { Active: false };
            }
            for (var i = 0; i < 100 && !await Complete(); i++) await Task.Delay(50);
            Assert.True(await Complete());
            Assert.Equal(32, (await h.RowsAsync()).Count);
            Assert.Equal(32, (await h.RowsAsync("moneypuck")).Count);
            Assert.Equal(9, h.Handler.Requests.Count);
            await Task.Delay(100);
            time.Advance(TimeSpan.FromHours(24).Subtract(TimeSpan.FromTicks(1)));
            Assert.Equal(0, await h.App.Services.GetRequiredService<MoneyPuckService>().RefreshNhlRatingsAsync(default));
            Assert.Equal(9, h.Handler.Requests.Count);
            h.Handler.Marker = time.GetUtcNow().AddTicks(1).AddMinutes(-5).ToString("O");
            time.Advance(TimeSpan.FromTicks(1));
            for (var i = 0; i < 100 && (h.Handler.Requests.Count != 18 || !await Complete()); i++) await Task.Delay(50);
            Assert.Equal(18, h.Handler.Requests.Count);
            Assert.True(await Complete());
            Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }
}
