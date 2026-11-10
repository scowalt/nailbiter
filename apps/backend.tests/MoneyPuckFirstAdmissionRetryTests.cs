using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using backend.Data;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Xunit;

namespace backend.tests;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class MoneyPuckFirstAdmissionRetryTests
{
    private static readonly TimeSpan AdmissionRetry = TimeSpan.FromHours(1);
    private static readonly TimeSpan DailyInterval = TimeSpan.FromHours(24);

    [Theory]
    [InlineData("own fault")]
    [InlineData("database failure")]
    public async Task Failed_first_admission_retries_hourly_and_recovers(string failure)
    {
        await using var app = await WorkerApp.StartAsync();
        await app.BreakAdmissionAsync(failure);
        await app.StartWorkerAsync();

        Assert.Equal(AdmissionRetry, await app.NextWaitAsync());
        AssertAdmissionErrors(await app.SentEventsAsync(), 1, failure);

        app.Clock.Advance(AdmissionRetry);
        Assert.Equal(AdmissionRetry, await app.NextWaitAsync());
        AssertAdmissionErrors(await app.SentEventsAsync(), 2, failure);
        Assert.Empty(app.Harness.Handler.Requests);

        await app.RepairAdmissionAsync(failure);
        app.Harness.Handler.Marker = app.Clock.GetUtcNow().Add(AdmissionRetry).AddMinutes(-5).ToString("O");
        app.Clock.Advance(AdmissionRetry);
        Assert.Equal(DailyInterval, await app.NextWaitAsync());
        Assert.Equal(32, (await app.Harness.RowsAsync()).Count);
        Assert.Equal(32, (await app.Harness.RowsAsync("moneypuck")).Count);
        AssertAdmissionErrors(await app.SentEventsAsync(), 2, failure);
    }

    [Theory]
    [InlineData("own fault")]
    [InlineData("database failure")]
    public async Task Shutdown_during_the_retry_wait_is_quiet(string failure)
    {
        await using var app = await WorkerApp.StartAsync();
        await app.BreakAdmissionAsync(failure);
        await app.StartWorkerAsync();
        Assert.Equal(AdmissionRetry, await app.NextWaitAsync());

        await app.StopWorkerAsync();

        Assert.True(app.Worker.ExecuteTask!.IsCompletedSuccessfully);
        AssertAdmissionErrors(await app.SentEventsAsync(), 1, failure);
    }

    [Theory]
    [InlineData("own fault")]
    [InlineData("database failure")]
    public async Task Route_refresh_between_failed_attempts_holds_the_loop_to_the_daily_limit(string failure)
    {
        await using var app = await WorkerApp.StartAsync();
        await app.BreakAdmissionAsync(failure);
        await app.StartWorkerAsync();
        Assert.Equal(AdmissionRetry, await app.NextWaitAsync());

        await app.RepairAdmissionAsync(failure);
        app.Clock.Advance(TimeSpan.FromMinutes(10));
        app.Harness.Handler.Marker = app.Clock.GetUtcNow().AddMinutes(-5).ToString("O");
        var routed = await app.Harness.App.Services.GetRequiredService<MoneyPuckService>().RefreshNhlAsync(CancellationToken.None);
        Assert.Equal((32, 32), (routed.Qualification, routed.Rankings));
        var fetched = app.Harness.Handler.Requests.Count;
        Assert.NotEqual(0, fetched);

        app.Clock.Advance(TimeSpan.FromMinutes(50));
        Assert.Equal(DailyInterval - TimeSpan.FromMinutes(50), await app.NextWaitAsync());
        Assert.Equal(fetched, app.Harness.Handler.Requests.Count);
        AssertAdmissionErrors(await app.SentEventsAsync(), 1, failure);
    }

    [Fact]
    public async Task Successful_first_admission_keeps_the_daily_cadence()
    {
        await using var app = await WorkerApp.StartAsync();
        await app.StartWorkerAsync();

        Assert.Equal(DailyInterval, await app.NextWaitAsync());
        Assert.Equal(32, (await app.Harness.RowsAsync()).Count);
        Assert.Empty(await app.SentEventsAsync());
    }

    private static void AssertAdmissionErrors(IReadOnlyCollection<SentEvent> sent, int count, string failure)
    {
        var exception = failure == "own fault" ? typeof(InvalidOperationException) : typeof(PostgresException);
        Assert.Equal(count, sent.Count);
        Assert.All(sent, e =>
        {
            Assert.Equal((typeof(MoneyPuckRefreshGate).FullName, "error"), (e.Logger, e.Level));
            Assert.Contains(e.Exceptions, x => x.Type == exception.FullName);
        });
    }

    private sealed record SentEvent(string? Logger, string? Level, (string? Type, string? Value)[] Exceptions);

    private sealed class WorkerApp : IAsyncDisposable
    {
        private readonly SentTransport transport = new();
        private MoneyPuckRefreshBackgroundService? worker;

        private WorkerApp() => Clock = new LoopClock(Harness.Clock.Now);

        public MoneyPuckQualificationHarness Harness { get; } = new();

        public LoopClock Clock { get; }

        public MoneyPuckRefreshBackgroundService Worker => worker!;

        public static async Task<WorkerApp> StartAsync()
        {
            var app = new WorkerApp();
            await app.Harness.StartAsync(configureServices: services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(app.Clock);
                services.PostConfigure<SentryAspNetCoreOptions>(options =>
                {
                    options.Dsn = "https://public@sentry.invalid/1";
                    options.Transport = app.transport;
                });
            });
            return app;
        }

        public Task BreakAdmissionAsync(string failure) => ExecuteAsync(failure == "own fault"
            ? """
              DELETE FROM moneypuck_refresh_state;
              CREATE FUNCTION skip_insert() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NULL; END $$;
              CREATE TRIGGER skip_insert BEFORE INSERT ON moneypuck_refresh_state FOR EACH ROW EXECUTE FUNCTION skip_insert();
              """
            : "ALTER TABLE moneypuck_refresh_state RENAME TO moneypuck_refresh_state_offline");

        public Task RepairAdmissionAsync(string failure) => ExecuteAsync(failure == "own fault"
            ? "DROP TRIGGER skip_insert ON moneypuck_refresh_state"
            : "ALTER TABLE moneypuck_refresh_state_offline RENAME TO moneypuck_refresh_state");

        public async Task StartWorkerAsync()
        {
            worker = ActivatorUtilities.CreateInstance<MoneyPuckRefreshBackgroundService>(Harness.App.Services);
            await worker.StartAsync(CancellationToken.None);
        }

        public async Task StopWorkerAsync() => await Worker.StopAsync(CancellationToken.None);

        public async Task<TimeSpan> NextWaitAsync() =>
            await Clock.LoopWaits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        public async Task<IReadOnlyCollection<SentEvent>> SentEventsAsync()
        {
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
            return transport.Events.ToArray();
        }

        private async Task ExecuteAsync(string sql)
        {
            using var scope = Harness.App.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.ExecuteSqlRawAsync(sql);
        }

        public async ValueTask DisposeAsync()
        {
            if (worker is not null)
            {
                await worker.StopAsync(CancellationToken.None);
                worker.Dispose();
            }
            await Harness.DisposeAsync();
        }
    }

    private sealed class LoopClock(DateTimeOffset now) : FakeTimeProvider(now)
    {
        public Channel<TimeSpan> LoopWaits { get; } = Channel.CreateUnbounded<TimeSpan>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (state is not CancellationTokenSource) LoopWaits.Writer.TryWrite(dueTime);
            return timer;
        }
    }

    private sealed class SentTransport : ITransport
    {
        public ConcurrentQueue<SentEvent> Events { get; } = new();

        public async Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            using var stream = new MemoryStream();
            await envelope.SerializeAsync(stream, null, cancellationToken);
            var lines = System.Text.Encoding.UTF8.GetString(stream.ToArray()).Split('\n');
            for (var i = 1; i + 1 < lines.Length; i++)
            {
                using var header = JsonDocument.Parse(lines[i]);
                if (header.RootElement.GetProperty("type").GetString() != "event") continue;
                using var payload = JsonDocument.Parse(lines[++i]);
                var root = payload.RootElement;
                var exceptions = root.TryGetProperty("exception", out var exception)
                    ? exception.GetProperty("values").EnumerateArray().Select(e => (Text(e, "type"), Text(e, "value"))).ToArray()
                    : [];
                Events.Enqueue(new SentEvent(Text(root, "logger"), Text(root, "level"), exceptions));
            }
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) ? value.GetString() : null;
    }
}
