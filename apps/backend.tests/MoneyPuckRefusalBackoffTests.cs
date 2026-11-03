using System.Net;
using System.Net.Http.Headers;
using backend.Data;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace backend.tests;

public sealed class MoneyPuckRefusalBackoffTests
{
    [Fact]
    public async Task A_second_refusal_starts_a_new_window_and_success_restores_the_normal_daily_budget()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        h.Handler.Override = (_, _) => Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.Forbidden));
        var service = h.App.Services.GetRequiredService<MoneyPuckService>();
        var first = await service.RefreshNhlAsync(default);
        h.Clock.Now = new DateTimeOffset(Assert.IsType<DateTime>(first.NextAttemptAt));
        var second = await service.RefreshNhlAsync(default);
        Assert.Equal<DateTime?>(h.Clock.Now.UtcDateTime.AddHours(24), second.NextAttemptAt);
        Assert.Equal(2, h.Handler.Requests.Count);
        await h.RestartApplicationAsync();
        h.Handler.Override = null;
        h.Clock.Now = new DateTimeOffset(Assert.IsType<DateTime>(second.NextAttemptAt)).AddTicks(-1);
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(2, h.Handler.Requests.Count);
        h.Clock.Now = h.Clock.Now.AddTicks(1);
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(11, h.Handler.Requests.Count);
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(11, h.Handler.Requests.Count);
    }

    [Fact]
    public async Task Expired_refusal_admits_only_one_competing_backend_instance()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        h.Handler.Override = (_, _) => Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.Forbidden));
        var first = await h.App.Services.GetRequiredService<MoneyPuckService>().RefreshNhlAsync(default);
        await using var peer = h.CreatePeerApplication();
        h.Clock.Now = new DateTimeOffset(Assert.IsType<DateTime>(first.NextAttemptAt));
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = true;
        h.Handler.Override = async (_, ct) =>
        {
            if (wait) { wait = false; entered.SetResult(); await release.Task.WaitAsync(ct); }
            return null;
        };
        var recovering = h.Service.RefreshNhlRatingsAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, (await peer.Services.GetRequiredService<MoneyPuckService>().RefreshNhlAsync(default)).Qualification);
            Assert.Equal(2, h.Handler.Requests.Count);
            release.SetResult();
            Assert.Equal(32, await recovering);
            Assert.Equal(10, h.Handler.Requests.Count);
            Assert.Equal(0, (await peer.Services.GetRequiredService<MoneyPuckService>().RefreshNhlAsync(default)).Qualification);
            Assert.Equal(10, h.Handler.Requests.Count);
        }
        finally { release.TrySetResult(); await recovering; }
    }

    [Fact]
    public async Task Expiring_a_refusal_does_not_bypass_a_later_daily_admission_deadline()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        var refusalDue = h.Clock.Now.AddHours(24);
        var dailyDue = h.Clock.Now.AddHours(48);
        using (var scope = h.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO moneypuck_refresh_state (id, next_attempt_at_ticks, active, denied, denied_until_ticks) VALUES (1, {dailyDue.UtcTicks}, FALSE, TRUE, {refusalDue.UtcTicks})");
        }
        h.Clock.Now = refusalDue;
        var blocked = await h.App.Services.GetRequiredService<MoneyPuckService>().RefreshNhlAsync(default);
        Assert.Equal(0, blocked.Qualification);
        Assert.Equal<DateTime?>(dailyDue.UtcDateTime, blocked.NextAttemptAt);
        Assert.Empty(h.Handler.Requests);
        h.Clock.Now = dailyDue;
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(9, h.Handler.Requests.Count);
    }

    [Fact]
    public async Task Malformed_Retry_After_still_records_a_full_refusal_window()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        h.Handler.Override = (_, _) =>
        {
            h.Clock.Now = h.Clock.Now.AddSeconds(30);
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("Retry-After", "not-a-date");
            return Task.FromResult<HttpResponseMessage?>(response);
        };
        var result = await h.App.Services.GetRequiredService<MoneyPuckService>().RefreshNhlAsync(default);
        Assert.Equal<DateTime?>(h.Clock.Now.UtcDateTime.AddHours(24), result.NextAttemptAt);
        Assert.Single(h.Handler.Requests);
    }

    private sealed class BackoffClock(DateTimeOffset now) : FakeTimeProvider(now)
    {
        public TaskCompletionSource Scheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime >= TimeSpan.FromHours(24)) Scheduled.TrySetResult();
            return timer;
        }
    }

    [Fact]
    public async Task Actual_background_worker_retries_at_refusal_expiry_without_a_manual_refresh()
    {
        await using var h = new MoneyPuckQualificationHarness();
        var clock = new BackoffClock(h.Clock.Now);
        await h.StartAsync(configureServices: services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); });
        h.Handler.Override = (_, _) => Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.Forbidden));
        using var worker = ActivatorUtilities.CreateInstance<MoneyPuckRefreshBackgroundService>(h.App.Services);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await clock.Scheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(h.Handler.Requests);
            h.Handler.Override = null;
            clock.Advance(TimeSpan.FromHours(24).Subtract(TimeSpan.FromTicks(1)));
            Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
            Assert.Single(h.Handler.Requests);
            h.Handler.Marker = clock.GetUtcNow().AddTicks(1).AddMinutes(-5).ToString("O");
            clock.Advance(TimeSpan.FromTicks(1));
            for (var i = 0; i < 100 && (await h.RowsAsync("moneypuck")).Count != 32; i++) await Task.Delay(50);
            Assert.Equal(32, (await h.RowsAsync()).Count);
            Assert.Equal(32, (await h.RowsAsync("moneypuck")).Count);
            await worker.StopAsync(CancellationToken.None);
            Assert.Equal(10, h.Handler.Requests.Count);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Migration_initializes_unknown_legacy_refusals_once_without_resetting_budget_or_active_state(bool active, bool denied)
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        using var scope = h.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE moneypuck_refresh_state");
        var original = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "033_MoneyPuckDailyRefresh.sql"));
        await db.Database.ExecuteSqlRawAsync(original);
        var budget = h.Clock.Now.UtcDateTime.AddHours(24).Ticks;
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO moneypuck_refresh_state (id, next_attempt_at_ticks, active, denied) VALUES (1, {budget}, {active}, {denied})");
        var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "036_MoneyPuckRefusalExpiry.sql"));
        var earliest = DateTime.UtcNow.AddHours(24).Ticks;
        await db.Database.ExecuteSqlRawAsync(migration);
        var latest = DateTime.UtcNow.AddHours(24).Ticks;
        var migrated = await db.MoneyPuckRefreshOperations.AsNoTracking().SingleAsync();
        Assert.Equal(active, migrated.Active);
        Assert.Equal(denied, migrated.Denied);
        Assert.Equal(budget, migrated.NextAttemptAtTicks);
        if (denied)
        {
            Assert.NotNull(migrated.DeniedUntilTicks);
            Assert.InRange(migrated.DeniedUntilTicks.Value, earliest, latest);
        }
        else Assert.Null(migrated.DeniedUntilTicks);
        await db.Database.ExecuteSqlRawAsync(migration);
        var repeated = await db.MoneyPuckRefreshOperations.AsNoTracking().SingleAsync();
        Assert.Equal(migrated.DeniedUntilTicks, repeated.DeniedUntilTicks);
        Assert.Equal(budget, repeated.NextAttemptAtTicks);
        Assert.Equal(active, repeated.Active);
        Assert.Equal(denied, repeated.Denied);

        var due = denied ? migrated.DeniedUntilTicks!.Value : budget;
        h.Clock.Now = new DateTimeOffset(due - 1, TimeSpan.Zero);
        Assert.Equal(0, await h.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        Assert.Empty(h.Handler.Requests);
        h.Clock.Now = h.Clock.Now.AddTicks(1);
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        Assert.Equal(active ? 0 : 32, await h.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        Assert.Equal(active ? 0 : 9, h.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(false, 1, 24)]
    [InlineData(false, 48, 48)]
    [InlineData(true, 48, 48)]
    public async Task Retry_After_cannot_shorten_the_day_and_longer_server_backoff_survives_restart(bool absolute, int headerHours, int expectedHours)
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        var observed = h.Clock.Now;
        h.Handler.Override = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = absolute
                ? new RetryConditionHeaderValue(observed.AddHours(headerHours))
                : new RetryConditionHeaderValue(TimeSpan.FromHours(headerHours));
            return Task.FromResult<HttpResponseMessage?>(response);
        };
        var result = await h.App.Services.GetRequiredService<MoneyPuckService>().RefreshNhlAsync(default);
        var retryAt = observed.AddHours(expectedHours);
        Assert.Equal<DateTime?>(retryAt.UtcDateTime, result.NextAttemptAt);
        await h.RestartApplicationAsync();
        h.Handler.Override = null;
        h.Clock.Now = retryAt.AddTicks(-1);
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Single(h.Handler.Requests);
        h.Clock.Now = retryAt;
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(10, h.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refusal_expires_exactly_24_hours_after_observation_without_replenishing_the_daily_budget(bool restart)
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        var admitted = h.Clock.Now;
        h.Handler.Override = (_, _) =>
        {
            h.Clock.Now = h.Clock.Now.AddSeconds(30);
            return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.Forbidden));
        };
        var denied = await h.App.Services.GetRequiredService<MoneyPuckService>().RefreshNhlAsync(default);
        var refused = admitted.AddSeconds(30);
        var retryAt = refused.AddHours(24);
        Assert.Equal(0, denied.Qualification);
        Assert.Equal(0, denied.Rankings);
        Assert.Equal<DateTime?>(retryAt.UtcDateTime, denied.NextAttemptAt);
        Assert.Single(h.Handler.Requests);

        h.Handler.Override = null;
        if (restart) await h.RestartApplicationAsync();
        var collector = h.App.Services.GetRequiredService<MoneyPuckService>();
        h.Clock.Now = retryAt.AddTicks(-1);
        var early = await collector.RefreshNhlAsync(default);
        Assert.Equal(0, early.Qualification);
        Assert.Equal(0, early.Rankings);
        Assert.Equal<DateTime?>(retryAt.UtcDateTime, early.NextAttemptAt);
        Assert.Single(h.Handler.Requests);

        h.Clock.Now = retryAt;
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        var recovered = await collector.RefreshNhlAsync(default);
        Assert.Equal(32, recovered.Qualification);
        Assert.Equal(32, recovered.Rankings);
        Assert.Equal<DateTime?>(retryAt.UtcDateTime.AddHours(24), recovered.NextAttemptAt);
        Assert.Equal(10, h.Handler.Requests.Count);
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(10, h.Handler.Requests.Count);
    }
}
