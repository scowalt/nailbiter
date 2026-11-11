using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using backend.Data;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Xunit;

namespace backend.tests;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class RatingsRefreshReportingTests
{
    private const string WorkerFailure = "Kalshi playoff refresh failed";

    [Fact]
    public async Task Failed_refresh_is_reported_once_at_error_and_the_worker_runs_the_next_cycle()
    {
        await using var app = await RefreshApp.StartAsync();
        await app.ExecuteSqlAsync("DROP TABLE college_teams");

        await app.Worker.StartAsync(CancellationToken.None);
        var first = Assert.Single(await app.WaitForSentAsync(1));
        app.Time.Advance(PlayoffRatingPolicy.refreshInterval);
        var sent = await app.WaitForSentAsync(2);

        Assert.Equal(("error", WorkerFailure), (first.Level, first.Message));
        Assert.Equal(2, sent.Count);
        Assert.All(sent, e => Assert.Equal(("error", WorkerFailure), (e.Level, e.Message)));
        var reported = app.Events.Where(e => e.Message == WorkerFailure).ToArray();
        Assert.Equal(2, reported.Length);
        Assert.All(reported, e => Assert.Equal(PostgresErrorCodes.UndefinedTable, Assert.IsType<PostgresException>(e.Exception).SqlState));
    }

    [Fact]
    public async Task Provider_timeout_in_a_refresh_is_sent_once_at_error_and_not_treated_as_shutdown()
    {
        await using var app = await RefreshApp.StartAsync(db => db.AddInterceptors(new ProviderTimeoutOnCollegeTeamsRead()));

        await app.Worker.StartAsync(CancellationToken.None);
        var sent = await app.WaitForSentAsync(1);

        var reported = Assert.Single(sent);
        Assert.Equal(("error", WorkerFailure), (reported.Level, reported.Message));
        Assert.Contains(typeof(TaskCanceledException).FullName, reported.ExceptionTypes);
        Assert.False(app.Worker.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public async Task Host_stop_during_a_refresh_ends_the_worker_without_an_error_event()
    {
        await using var app = await RefreshApp.StartAsync();
        await using var teamsLock = await app.LockCollegeTeamsAsync();

        await app.Worker.StartAsync(CancellationToken.None);
        await teamsLock.WaitForBlockedQueryAsync();
        await app.Worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(app.Worker.ExecuteTask!.IsCompletedSuccessfully);
        Assert.DoesNotContain(app.Events, e => e.Level >= SentryLevel.Error);
        Assert.Empty(await app.SentEventsAsync());
    }

    private sealed record ReportedEvent(SentryLevel? Level, string? Message, Exception? Exception);

    private sealed record SentEvent(string? Level, string? Message, string?[] ExceptionTypes);

    private sealed class RefreshApp : IAsyncDisposable
    {
        private readonly MoneyPuckQualificationHarness harness = new();
        private readonly SentryRecorder recorder = new();
        private KalshiPlayoffRefreshBackgroundService? worker;

        public EmptyProviders Providers { get; } = new();
        public IEnumerable<ReportedEvent> Events => recorder.Events;
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 11, 18, 0, 0, TimeSpan.Zero));

        public KalshiPlayoffRefreshBackgroundService Worker => worker ??= new(
            harness.App.Services, Time, harness.App.Services.GetRequiredService<ILogger<KalshiPlayoffRefreshBackgroundService>>());

        public static async Task<RefreshApp> StartAsync(Action<DbContextOptionsBuilder>? configureDb = null)
        {
            var app = new RefreshApp();
            await app.harness.StartAsync(configureDb, enabled: false, configuredTransport: true, configureServices: services =>
            {
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = app.Providers));
                services.PostConfigure<SentryAspNetCoreOptions>(options =>
                {
                    options.Dsn = "https://public@sentry.invalid/1";
                    options.Transport = app.recorder;
                    options.AddEventProcessor(app.recorder);
                });
            });
            return app;
        }

        public async Task<IReadOnlyCollection<SentEvent>> SentEventsAsync()
        {
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
            return recorder.Sent.ToArray();
        }

        public async Task<IReadOnlyCollection<SentEvent>> WaitForSentAsync(int count)
        {
            var deadline = Stopwatch.StartNew();
            while (recorder.Sent.Count < count)
            {
                Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(60), $"expected {count} sent events, saw {recorder.Sent.Count}");
                await SentrySdk.FlushAsync(TimeSpan.FromMilliseconds(200));
                await Task.Delay(50);
            }
            return await SentEventsAsync();
        }

        public async Task ExecuteSqlAsync(string sql)
        {
            using var scope = harness.App.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.ExecuteSqlRawAsync(sql);
        }

        public async Task<TableLock> LockCollegeTeamsAsync()
        {
            using var scope = harness.App.Services.CreateScope();
            var connectionString = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.GetConnectionString()!;
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            var transaction = await connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand("LOCK TABLE college_teams IN ACCESS EXCLUSIVE MODE", connection, transaction);
            await command.ExecuteNonQueryAsync();
            return new TableLock(connectionString, connection, transaction);
        }

        public async ValueTask DisposeAsync()
        {
            if (worker is not null)
            {
                await worker.StopAsync(CancellationToken.None);
                worker.Dispose();
            }
            await harness.DisposeAsync();
            Providers.Dispose();
        }
    }

    private sealed class TableLock(string connectionString, NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async Task WaitForBlockedQueryAsync()
        {
            await using var observer = new NpgsqlConnection(connectionString);
            await observer.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query ILIKE '%college_teams%'",
                observer);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while ((long)(await command.ExecuteScalarAsync(timeout.Token))! == 0)
                await Task.Delay(50, timeout.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class ProviderTimeoutOnCollegeTeamsRead : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("college_teams", StringComparison.Ordinal))
            {
                using var provider = new HttpClient(new HangingProvider()) { Timeout = TimeSpan.FromMilliseconds(100) };
                await provider.GetAsync("https://provider.invalid/", cancellationToken);
            }
            return result;
        }
    }

    private sealed class HangingProvider : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        }
    }

    private sealed class EmptyProviders : HttpMessageHandler
    {
        public ConcurrentQueue<string> Urls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Enqueue(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") });
        }
    }

    private sealed class SentryRecorder : ITransport, ISentryEventProcessor
    {
        public ConcurrentQueue<ReportedEvent> Events { get; } = new();
        public ConcurrentQueue<SentEvent> Sent { get; } = new();

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
                var message = root.TryGetProperty("logentry", out var m) && m.TryGetProperty("formatted", out var f) ? f.GetString() : null;
                var exceptionTypes = root.TryGetProperty("exception", out var exception)
                    ? exception.GetProperty("values").EnumerateArray().Select(e => e.GetProperty("type").GetString()).ToArray()
                    : [];
                Sent.Enqueue(new SentEvent(root.GetProperty("level").GetString(), message, exceptionTypes));
            }
        }

        public SentryEvent? Process(SentryEvent sentryEvent)
        {
            Events.Enqueue(new ReportedEvent(sentryEvent.Level, sentryEvent.Message?.Formatted, sentryEvent.Exception));
            return sentryEvent;
        }
    }
}
