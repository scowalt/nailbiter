using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using backend.Data;
using backend.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Polly.Timeout;
using Sentry;
using Sentry.AspNetCore;
using Xunit;
using static backend.tests.DatabaseFailureReportingTests;

namespace backend.tests;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class CollegeLiveLockReportingTests
{
    private const string CollectorLock = "SELECT pg_advisory_xact_lock(674213608)";
    private const int Ticks = 3;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Live_ticks_refused_by_a_running_import_send_no_event_and_the_loop_collects_once_the_lock_is_free()
    {
        await using var database = await CollegeHistoryDatabase.StartAsync(1);
        var held = await database.HoldAsync(CollectorLock);
        var clock = new FakeTimeProvider(CollegeHistoryProvider.Now);
        await using var app = new LiveLoop(database, clock);

        await app.AdvanceUntilAsync(() => Task.FromResult(app.LoopLogs.Count() >= Ticks));
        var sent = await app.SentEventsAsync();

        Assert.True(sent.Count == 0, $"{app.LoopLogs.Count()} refused ticks sent {sent.Count} events: "
            + string.Join("; ", sent.Select(e => $"{e.Logger}/{e.Level}/{string.Join(",", e.Exceptions.Select(x => x.Type))}")));
        Assert.All(app.LoopLogs, entry => Assert.True(entry.Level < LogLevel.Warning, $"refused tick logged at {entry.Level}"));
        Assert.Empty(database.Provider.Requests);

        await held.DisposeAsync();
        await app.AdvanceUntilAsync(() => Task.FromResult(!database.Provider.Requests.IsEmpty));
        await app.WaitUntilAsync(async () => await database.CountAsync("college_imports") == 1);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Live_tick_that_fails_in_our_own_code_is_sent_once_at_error()
    {
        await using var database = await CollegeHistoryDatabase.StartAsync(1);
        database.Provider.Status = _ => throw new InvalidOperationException("Synthetic fault below the scoreboard fetch");
        await using var app = new LiveLoop(database, new FakeTimeProvider(CollegeHistoryProvider.Now));

        await app.WaitUntilAsync(() => Task.FromResult(app.LoopLogs.Any()));
        var sent = await app.SentEventsAsync();

        var reported = Assert.Single(sent);
        Assert.Equal((typeof(CollegeFootballBackgroundService).FullName, "error"), (reported.Logger, reported.Level));
        Assert.Contains(reported.Exceptions, e => e.Type == typeof(InvalidOperationException).FullName);
        Assert.Equal(LogLevel.Error, Assert.Single(app.LoopLogs).Level);
    }

    [Fact]
    public async Task Live_tick_whose_scoreboard_answers_404_logs_a_warning_and_sends_no_event()
    {
        await using var database = await CollegeHistoryDatabase.StartAsync(1);
        await using var app = new LiveLoop(database, new FakeTimeProvider(CollegeHistoryProvider.Now),
            (uri, _) => Task.FromResult(IsScoreboard(uri) ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") } : null));

        await app.WaitUntilAsync(() => Task.FromResult(app.LoopLogs.Any()));

        Assert.Empty(await app.SentEventsAsync());
        Assert.Equal(LogLevel.Warning, Assert.Single(app.LoopLogs).Level);
    }

    [Fact]
    public async Task Live_tick_whose_scoreboard_body_is_malformed_logs_a_warning_and_sends_no_event()
    {
        await using var database = await CollegeHistoryDatabase.StartAsync(1);
        await using var app = new LiveLoop(database, new FakeTimeProvider(CollegeHistoryProvider.Now),
            (uri, _) => Task.FromResult(IsScoreboard(uri) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"events\": [") } : null));

        await app.WaitUntilAsync(() => Task.FromResult(app.LoopLogs.Any()));

        Assert.Empty(await app.SentEventsAsync());
        Assert.Equal(LogLevel.Warning, Assert.Single(app.LoopLogs).Level);
    }

    [Fact]
    public async Task Live_tick_whose_scoreboard_answers_503_is_sent_once_at_error()
    {
        await using var database = await CollegeHistoryDatabase.StartAsync(1);
        await using var app = new LiveLoop(database, new FakeTimeProvider(CollegeHistoryProvider.Now),
            (uri, _) => Task.FromResult(IsScoreboard(uri) ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") } : null));

        await app.WaitUntilAsync(() => Task.FromResult(app.LoopLogs.Any()));
        var sent = await app.SentEventsAsync();

        var reported = Assert.Single(sent);
        Assert.Equal((typeof(CollegeFootballBackgroundService).FullName, "error"), (reported.Logger, reported.Level));
        Assert.Contains(reported.Exceptions, e => e.Type == typeof(HttpRequestException).FullName);
        Assert.Equal(LogLevel.Error, Assert.Single(app.LoopLogs).Level);
    }

    [Fact]
    public async Task Live_tick_whose_scoreboard_request_times_out_is_sent_once_at_error()
    {
        await using var database = await CollegeHistoryDatabase.StartAsync(1);
        await using var app = new LiveLoop(database, new FakeTimeProvider(CollegeHistoryProvider.Now),
            async (uri, ct) =>
            {
                if (!IsScoreboard(uri)) return null;
                await Task.Delay(Timeout.Infinite, ct);
                return null;
            });

        await app.AdvanceUntilAsync(() => Task.FromResult(app.LoopLogs.Any()));
        var sent = await app.SentEventsAsync();

        var reported = Assert.Single(sent);
        Assert.Equal((typeof(CollegeFootballBackgroundService).FullName, "error"), (reported.Logger, reported.Level));
        Assert.Contains(reported.Exceptions, e => e.Type == typeof(TimeoutRejectedException).FullName);
        Assert.Equal(LogLevel.Error, Assert.Single(app.LoopLogs).Level);
    }

    private static bool IsScoreboard(Uri uri) => uri.AbsolutePath.EndsWith("/scoreboard", StringComparison.Ordinal);

    private sealed record LoggedEntry(string Category, LogLevel Level);

    private sealed class LiveLoop : IAsyncDisposable
    {
        private readonly AppFactory entry = new();
        private readonly WebApplicationFactory<Program> app;
        private readonly SentTransport transport = new();
        private readonly LogRecorder logs = new();
        private readonly FakeTimeProvider clock;

        public LiveLoop(CollegeHistoryDatabase database, FakeTimeProvider clock,
            Func<Uri, CancellationToken, Task<HttpResponseMessage?>>? respond = null)
        {
            this.clock = clock;
            app = entry.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.ConfigureServices(services =>
                {
                    foreach (var other in services.Where(s => s.ServiceType == typeof(IHostedService)
                        && s.ImplementationType?.Namespace?.StartsWith("backend", StringComparison.Ordinal) == true).ToArray())
                        services.Remove(other);
                    services.AddSingleton<IHostedService, CollegeFootballBackgroundService>();
                    services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
                    services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
                    services.AddDbContext<ExcitementDbContext>(options => options.UseNpgsql(database.ConnectionString));
                    services.ConfigureAll<HttpClientFactoryOptions>(options =>
                        options.HttpMessageHandlerBuilderActions.Add(handlers => handlers.PrimaryHandler = new Forwarding(database.Provider,
                            respond ?? ((_, _) => Task.FromResult<HttpResponseMessage?>(null)))));
                    services.ConfigureAll<HttpStandardResilienceOptions>(options =>
                    {
                        options.Retry.Delay = TimeSpan.Zero;
                        options.AttemptTimeout.Timeout = TimeSpan.FromMinutes(5);
                        options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(10);
                        options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(10);
                    });
                    services.AddSingleton<ILoggerProvider>(logs);
                    services.PostConfigure<SentryAspNetCoreOptions>(options =>
                    {
                        options.Dsn = "https://public@sentry.invalid/1";
                        options.Transport = transport;
                    });
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(clock);
                });
            });
            _ = app.Services;
        }

        public IEnumerable<LoggedEntry> LoopLogs =>
            logs.Entries.Where(l => l.Category == typeof(CollegeFootballBackgroundService).FullName);

        public Task AdvanceUntilAsync(Func<Task<bool>> reached) => PollAsync(reached, true);

        public Task WaitUntilAsync(Func<Task<bool>> reached) => PollAsync(reached, false);

        private async Task PollAsync(Func<Task<bool>> reached, bool advance)
        {
            var waited = Stopwatch.StartNew();
            while (!await reached())
            {
                Assert.True(waited.Elapsed < Deadline, $"the college loop did not reach the expected state within {Deadline}");
                await Task.Delay(50);
                if (advance && !await reached()) clock.Advance(TimeSpan.FromSeconds(30));
            }
        }

        public async Task<IReadOnlyCollection<SentEvent>> SentEventsAsync()
        {
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
            return transport.Events.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            await entry.DisposeAsync();
        }
    }

    private sealed class Forwarding(HttpMessageHandler inner, Func<Uri, CancellationToken, Task<HttpResponseMessage?>> respond)
        : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            await respond(request.RequestUri!, cancellationToken) ?? await base.SendAsync(request, cancellationToken);

        protected override void Dispose(bool disposing) { }
    }

    private sealed class LogRecorder : ILoggerProvider
    {
        public ConcurrentQueue<LoggedEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

        public void Dispose() { }

        private sealed class Logger(string category, ConcurrentQueue<LoggedEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LoggedEntry(category, logLevel));
        }
    }
}
