using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using backend.Data;
using backend.Models;
using backend.Services;
using backend.Services.PlayoffStatus;
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
using Npgsql;
using Polly.Timeout;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Testcontainers.PostgreSql;
using Xunit;

namespace backend.tests;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class PlayoffAndProHypeReportingTests
{
    private const int StandardAttempts = 4;
    private const string PlayoffStatusFailure = "Failed to refresh {Sport} ratings from PlayoffStatus";
    private const string NhlModelFailure = "Failed NHL PlayoffStatus model refresh for season {Season}";
    private const int NhlSeason = 2027;
    private const string ProCompetitionFailure = "Pro competition refresh failed";
    private const string ProHypeFailure = "Pro hype capture failed";
    private const string LockTimeout = ";Options=-c lock_timeout=500";
    private static readonly TimeSpan HangDeadline = TimeSpan.FromSeconds(60);
    private static readonly Answer Hang = new Hanging();
    private static readonly Answer Unavailable = new Reply(HttpStatusCode.ServiceUnavailable, "{}");
    private static readonly Answer Unexpected = new Throwing();
    private const string BillsStandings = """
        <table><tr data-idx="0"><td><a>Buffalo Bills</a></td><td>7</td><td>2</td><td>0</td><td>4%</td></tr></table>
        """;

    private static readonly Dictionary<string, Func<PlayoffStatusService, CancellationToken, Task<int>>> PlayoffStatusRefreshes = new()
    {
        ["nfl"] = (service, ct) => service.RefreshNflRatingsAsync(ct),
        ["nba"] = (service, ct) => service.RefreshNbaRatingsAsync(ct),
    };

    public static TheoryData<string, HttpStatusCode, string> UnreadablePlayoffStatusPages()
    {
        var cases = new TheoryData<string, HttpStatusCode, string>();
        foreach (var sport in PlayoffStatusRefreshes.Keys)
        {
            cases.Add(sport, HttpStatusCode.OK, "{}");
            cases.Add(sport, HttpStatusCode.OK, "");
            cases.Add(sport, HttpStatusCode.NotFound, "{}");
        }
        return cases;
    }

    [Theory]
    [InlineData("nfl")]
    [InlineData("nba")]
    public async Task Caller_cancellation_mid_playoffstatus_fetch_propagates_without_an_event(string sport)
    {
        await using var app = new ReportingApp(_ => Hang);
        using var caller = new CancellationTokenSource();

        var refreshing = RefreshPlayoffStatusAsync(app, sport, caller.Token);
        await app.Provider.HangStarted.WaitAsync(HangDeadline);
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refreshing);
        Assert.DoesNotContain(app.Logs, l => l.Category == typeof(PlayoffStatusService).FullName && l.Level >= LogLevel.Warning);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Theory]
    [MemberData(nameof(UnreadablePlayoffStatusPages))]
    public async Task Unreadable_or_refused_playoffstatus_page_is_a_warning_without_an_event(string sport, HttpStatusCode status, string body)
    {
        await using var app = new ReportingApp(_ => new Reply(status, body));

        Assert.Equal(0, await RefreshPlayoffStatusAsync(app, sport, CancellationToken.None));

        var logged = app.Logs.Where(l => l.Category == typeof(PlayoffStatusService).FullName && l.Level >= LogLevel.Warning).ToArray();
        Assert.NotEmpty(logged);
        Assert.All(logged, l => Assert.Equal(LogLevel.Warning, l.Level));
        Assert.Empty(await app.SentEventsAsync());
    }

    [Theory]
    [InlineData("nfl")]
    [InlineData("nba")]
    public async Task Playoffstatus_server_errors_after_retries_are_sent_once_at_error(string sport)
    {
        await using var app = new ReportingApp(_ => Unavailable);

        Assert.Equal(0, await RefreshPlayoffStatusAsync(app, sport, CancellationToken.None));

        Assert.Equal(StandardAttempts, app.Provider.Requests);
        AssertSentOnceAtError(await app.SentEventsAsync(), typeof(PlayoffStatusService), PlayoffStatusFailure, typeof(HttpRequestException));
    }

    [Theory]
    [InlineData("nfl")]
    [InlineData("nba")]
    public async Task Playoffstatus_timeout_after_retries_is_sent_once_at_error(string sport)
    {
        await using var app = new ReportingApp(_ => Hang, configure: services => services.ConfigureAll<HttpStandardResilienceOptions>(options =>
            options.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(50)));

        Assert.Equal(0, await RefreshPlayoffStatusAsync(app, sport, CancellationToken.None));

        Assert.Equal(StandardAttempts, app.Provider.Requests);
        AssertSentOnceAtError(await app.SentEventsAsync(), typeof(PlayoffStatusService), PlayoffStatusFailure, typeof(TimeoutRejectedException));
    }

    [Theory]
    [InlineData("nfl")]
    [InlineData("nba")]
    public async Task Playoffstatus_client_timeout_is_sent_once_at_error(string sport)
    {
        await using var app = new ReportingApp(_ => Hang, configure: services =>
            services.Configure<HttpClientFactoryOptions>(nameof(PlayoffStatusService), options =>
                options.HttpClientActions.Add(client => client.Timeout = TimeSpan.FromMilliseconds(50))));

        Assert.Equal(0, await RefreshPlayoffStatusAsync(app, sport, CancellationToken.None));

        AssertSentOnceAtError(await app.SentEventsAsync(), typeof(PlayoffStatusService), PlayoffStatusFailure, typeof(TaskCanceledException));
    }

    [Theory]
    [InlineData("nfl")]
    [InlineData("nba")]
    public async Task Playoffstatus_own_fault_after_parsing_is_sent_once_at_error(string sport)
    {
        await using var app = new ReportingApp(uri => new Reply(HttpStatusCode.OK, uri.AbsolutePath.EndsWith("standings.html", StringComparison.Ordinal) ? BillsStandings : ""),
            configure: services => services.RemoveAll<ExcitementDbContext>());

        Assert.Equal(0, await RefreshPlayoffStatusAsync(app, sport, CancellationToken.None));

        AssertSentOnceAtError(await app.SentEventsAsync(), typeof(PlayoffStatusService), PlayoffStatusFailure, typeof(InvalidOperationException));
    }

    [Fact]
    public async Task Playoffstatus_ratings_write_failure_is_sent_once_at_error()
    {
        await using var database = await Database.StartAsync();
        await using var tableLock = await database.HoldLockAsync("LOCK TABLE team_ratings IN ACCESS EXCLUSIVE MODE");
        await using var app = new ReportingApp(uri => new Reply(HttpStatusCode.OK, uri.AbsolutePath.EndsWith("standings.html", StringComparison.Ordinal) ? BillsStandings : ""),
            connectionString: database.ConnectionString + LockTimeout);

        Assert.Equal(0, await RefreshPlayoffStatusAsync(app, "nfl", CancellationToken.None));

        AssertSentOnceAtError(await app.SentEventsAsync(), typeof(PlayoffStatusService), PlayoffStatusFailure, typeof(PostgresException));
    }

    [Fact]
    public async Task Nhl_model_refresh_own_fault_is_sent_once_at_error()
    {
        var published = DateTime.UtcNow.AddHours(-24);
        await using var app = NhlModelApp(uri => new Reply(HttpStatusCode.OK, NflPlayoffPageTests.PlayoffStatusFixture(uri.AbsoluteUri, published, invalidBos: false)),
            services => services.RemoveAll<ExcitementDbContext>());

        Assert.Equal(0, await RefreshNhlModelAsync(app));

        Assert.Equal(3, app.Provider.Requests);
        AssertSentOnceAtError(await app.SentEventsAsync(), typeof(PlayoffStatusService), NhlModelFailure, typeof(InvalidOperationException));
    }

    [Fact]
    public async Task Nhl_model_refresh_refused_page_is_a_warning_without_an_event()
    {
        await using var app = NhlModelApp(_ => new Reply(HttpStatusCode.NotFound, "{}"));

        Assert.Equal(0, await RefreshNhlModelAsync(app));

        var failure = Assert.Single(app.Logs, l => l.Category == typeof(PlayoffStatusService).FullName && l.Level >= LogLevel.Warning);
        Assert.Equal((LogLevel.Warning, NhlModelFailure), (failure.Level, failure.Template));
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Nhl_model_refresh_server_errors_after_retries_are_sent_once_at_error()
    {
        await using var app = NhlModelApp(_ => Unavailable);

        Assert.Equal(0, await RefreshNhlModelAsync(app));

        Assert.Equal(StandardAttempts, app.Provider.Requests);
        AssertSentOnceAtError(await app.SentEventsAsync(), typeof(PlayoffStatusService), NhlModelFailure, typeof(HttpRequestException));
    }

    [Fact]
    public async Task Pro_competition_refresh_shutdown_stops_the_loop_without_an_event()
    {
        await using var database = await Database.StartAsync(ScheduledNbaGame());
        await using var app = new ReportingApp(_ => Hang, typeof(ProCompetitionRefreshBackgroundService), database.ConnectionString);
        await app.Provider.HangStarted.WaitAsync(HangDeadline);

        var sent = await app.StopAsync();

        Assert.DoesNotContain(app.Logs, l => l.Level >= LogLevel.Warning && l.Category.StartsWith("backend", StringComparison.Ordinal));
        Assert.Empty(sent);
    }

    [Fact]
    public async Task Unexpected_pro_competition_refresh_failure_is_sent_once_at_error_and_the_loop_keeps_running()
    {
        await using var database = await Database.StartAsync(ScheduledNbaGame());
        await using var app = new ReportingApp(_ => Unexpected, typeof(ProCompetitionRefreshBackgroundService), database.ConnectionString);

        await app.WaitForLogAsync(typeof(ProCompetitionRefreshBackgroundService), ProCompetitionFailure);

        AssertSentOnceAtError(await app.SentEventsAsync(), typeof(ProCompetitionRefreshBackgroundService), ProCompetitionFailure, typeof(NotSupportedException));
        var worker = app.Services.GetServices<IHostedService>().OfType<ProCompetitionRefreshBackgroundService>().Single();
        Assert.False(worker.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public async Task Pro_competition_timeout_after_retries_is_sent_once_at_error()
    {
        await using var database = await Database.StartAsync(ScheduledNbaGame());
        await using var app = new ReportingApp(_ => Hang, typeof(ProCompetitionRefreshBackgroundService), database.ConnectionString,
            services => services.ConfigureAll<HttpStandardResilienceOptions>(options => options.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(50)));

        await app.WaitForLogAsync(typeof(ProCompetitionRefreshBackgroundService), ProCompetitionFailure);

        Assert.Equal(StandardAttempts, app.Provider.Requests);
        AssertSentOnceAtError(await app.SentEventsAsync(), typeof(ProCompetitionRefreshBackgroundService), ProCompetitionFailure, typeof(TimeoutRejectedException));
    }

    [Fact]
    public async Task Pro_hype_capture_shutdown_stops_the_loop_without_an_event()
    {
        var lifecycle = new SyntheticLifecycle(ct => Task.Delay(Timeout.Infinite, ct));
        var clock = new FakeTimeProvider();
        await using var app = ProHypeApp(lifecycle, clock);
        await lifecycle.Captured(1).WaitAsync(HangDeadline);

        var sent = await app.StopAsync();

        Assert.DoesNotContain(app.Logs, l => l.Level >= LogLevel.Warning && l.Category.StartsWith("backend", StringComparison.Ordinal));
        Assert.Empty(sent);
    }

    [Fact]
    public async Task Unexpected_pro_hype_capture_failure_is_sent_once_at_error_and_the_loop_keeps_capturing()
    {
        var lifecycle = new SyntheticLifecycle(_ => throw new InvalidOperationException("synthetic capture failure"));
        var clock = new FakeTimeProvider();
        await using var app = ProHypeApp(lifecycle, clock);

        await lifecycle.Captured(1).WaitAsync(HangDeadline);
        await app.WaitForLogAsync(typeof(ProHypeCaptureBackgroundService), ProHypeFailure);
        AssertSentOnceAtError(await app.SentEventsAsync(), typeof(ProHypeCaptureBackgroundService), ProHypeFailure, typeof(InvalidOperationException));
        await AdvanceUntilAsync(clock, lifecycle.Captured(2));
    }

    [Fact]
    public async Task Pro_hype_capture_database_failure_is_sent_at_error_and_the_loop_keeps_capturing()
    {
        var lifecycle = new SyntheticLifecycle(_ => throw new PostgresException("synthetic deadlock", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected));
        var clock = new FakeTimeProvider();
        await using var app = ProHypeApp(lifecycle, clock);

        await lifecycle.Captured(1).WaitAsync(HangDeadline);
        await app.WaitForLogAsync(typeof(ProHypeCaptureBackgroundService), ProHypeFailure);
        AssertSentOnceAtError(await app.SentEventsAsync(), typeof(ProHypeCaptureBackgroundService), ProHypeFailure, typeof(PostgresException));
        await AdvanceUntilAsync(clock, lifecycle.Captured(2));
    }

    private static async Task<int> RefreshPlayoffStatusAsync(ReportingApp app, string sport, CancellationToken ct)
    {
        using var scope = app.Services.CreateScope();
        return await PlayoffStatusRefreshes[sport](scope.ServiceProvider.GetRequiredService<PlayoffStatusService>(), ct);
    }

    private static ReportingApp NhlModelApp(Func<Uri, Answer> answer, Action<IServiceCollection>? configure = null) =>
        new(answer, configure: services =>
        {
            services.PostConfigure<PlayoffRatingsOptions>(options =>
            {
                options.NhlModelMode = "fallback";
                options.NhlModelRefreshEnabled = true;
                options.ApprovedSeasons = [NhlSeason];
            });
            configure?.Invoke(services);
        });

    private static async Task<int> RefreshNhlModelAsync(ReportingApp app)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PlayoffStatusService>().RefreshNhlRatingsForSeasonAsync(NhlSeason, CancellationToken.None);
    }

    private static Game ScheduledNbaGame() => new()
    {
        Id = "401900001", Sport = "nba", Status = "scheduled", SeasonYear = 2027, SeasonType = 2,
        HomeTeam = "Boston Celtics", AwayTeam = "New York Knicks", HomeTeamAbbreviation = "BOS", AwayTeamAbbreviation = "NY",
        ScheduledStart = DateTime.UtcNow.AddDays(1)
    };

    private static ReportingApp ProHypeApp(SyntheticLifecycle lifecycle, FakeTimeProvider clock) =>
        new(_ => Unexpected, typeof(ProHypeCaptureBackgroundService), configure: services =>
        {
            services.RemoveAll<IProHypeLifecycle>();
            services.AddSingleton<IProHypeLifecycle>(lifecycle);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });

    private static async Task AdvanceUntilAsync(FakeTimeProvider clock, Task reached)
    {
        using var deadline = new CancellationTokenSource(HangDeadline);
        while (!reached.IsCompleted)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(50, deadline.Token);
        }
    }

    private static void AssertSentOnceAtError(IReadOnlyCollection<SentEvent> sent, Type logger, string template, Type exception)
    {
        var reported = Assert.Single(sent);
        Assert.Equal((logger.FullName, "error", template), (reported.Logger, reported.Level, reported.Template));
        Assert.Contains(reported.Exceptions, type => type == exception.FullName);
    }

    private abstract record Answer;

    private sealed record Reply(HttpStatusCode Status, string Body) : Answer;

    private sealed record Hanging : Answer;

    private sealed record Throwing : Answer;

    private sealed record SentEvent(string? Logger, string? Level, string? Template, string?[] Exceptions);

    private sealed record LoggedEntry(string Category, LogLevel Level, string? Template, Exception? Exception);

    private sealed class SyntheticLifecycle(Func<CancellationToken, Task> capture) : IProHypeLifecycle
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource> captures = new();
        private int count;

        public Task Captured(int times) => captures.GetOrAdd(times, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        public Task<ProHypeResolution> ResolveAsync(string gameId, CancellationToken ct = default) => throw new UnreachableException();

        public Task CaptureAsync(CancellationToken ct = default)
        {
            var times = Interlocked.Increment(ref count);
            for (var i = 1; i <= times; i++)
                captures.GetOrAdd(i, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            return capture(ct);
        }
    }

    private sealed class Database : IAsyncDisposable
    {
        private readonly PostgreSqlContainer container = IsolatedPostgreSql.CreateContainer();

        public string ConnectionString => container.GetConnectionString();

        public static async Task<Database> StartAsync(params Game[] games)
        {
            var database = new Database();
            await database.container.StartAsync();
            await using var db = new ExcitementDbContext(new DbContextOptionsBuilder<ExcitementDbContext>().UseNpgsql(database.ConnectionString).Options);
            await db.Database.EnsureCreatedAsync();
            db.Games.AddRange(games);
            await db.SaveChangesAsync();
            return database;
        }

        public async Task<IAsyncDisposable> HoldLockAsync(string sql)
        {
            var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            var transaction = await connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync();
            return new HeldLock(connection, transaction);
        }

        public ValueTask DisposeAsync() => container.DisposeAsync();
    }

    private sealed class HeldLock(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class ReportingApp : IAsyncDisposable
    {
        private readonly AppFactory entry = new();
        private readonly WebApplicationFactory<Program> app;
        private readonly SentTransport transport = new();
        private readonly LogRecorder logs = new();
        private bool stopped;

        public ReportingApp(Func<Uri, Answer> answer, Type? worker = null, string? connectionString = null, Action<IServiceCollection>? configure = null)
        {
            Provider = new SyntheticProvider(answer);
            app = entry.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.ConfigureServices(services =>
                {
                    foreach (var other in services.Where(s => s.ServiceType == typeof(IHostedService)
                        && s.ImplementationType?.Namespace?.StartsWith("backend", StringComparison.Ordinal) == true).ToArray())
                        services.Remove(other);
                    if (worker is not null) services.AddSingleton(typeof(IHostedService), worker);
                    if (connectionString is not null)
                    {
                        services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
                        services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
                        services.AddDbContext<ExcitementDbContext>(options => options.UseNpgsql(connectionString));
                    }
                    services.ConfigureAll<HttpClientFactoryOptions>(options =>
                        options.HttpMessageHandlerBuilderActions.Add(handlers => handlers.PrimaryHandler = Provider));
                    services.ConfigureAll<HttpStandardResilienceOptions>(options => options.Retry.Delay = TimeSpan.Zero);
                    services.AddSingleton<ILoggerProvider>(logs);
                    services.PostConfigure<SentryAspNetCoreOptions>(options =>
                    {
                        options.Dsn = "https://public@sentry.invalid/1";
                        options.Transport = transport;
                    });
                    configure?.Invoke(services);
                });
            });
            _ = app.Services;
        }

        public SyntheticProvider Provider { get; }

        public IServiceProvider Services => app.Services;

        public IEnumerable<LoggedEntry> Logs => logs.Entries;

        public async Task<LoggedEntry> WaitForLogAsync(Type category, string template)
        {
            using var deadline = new CancellationTokenSource(HangDeadline);
            while (true)
            {
                var entry = logs.Entries.FirstOrDefault(l => l.Category == category.FullName && l.Template == template);
                if (entry is not null) return entry;
                await Task.Delay(50, deadline.Token);
            }
        }

        public async Task<IReadOnlyCollection<SentEvent>> SentEventsAsync()
        {
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
            return transport.Events.ToArray();
        }

        public async Task<IReadOnlyCollection<SentEvent>> StopAsync()
        {
            await DisposeAsync();
            return transport.Events.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            if (stopped) return;
            stopped = true;
            await app.DisposeAsync();
            await entry.DisposeAsync();
            Provider.Dispose();
        }
    }

    private sealed class SyntheticProvider(Func<Uri, Answer> answer) : HttpMessageHandler
    {
        private readonly TaskCompletionSource hanging = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int requests;

        public Task HangStarted => hanging.Task;

        public int Requests => requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            switch (answer(request.RequestUri!))
            {
                case Reply reply:
                    return new HttpResponseMessage(reply.Status) { Content = new StringContent(reply.Body) };
                case Throwing:
                    throw new NotSupportedException("synthetic unexpected failure");
            }
            hanging.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        }
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
                entries.Enqueue(new LoggedEntry(category, logLevel, Template(state), exception));

            private static string? Template<TState>(TState state) =>
                state is IReadOnlyList<KeyValuePair<string, object?>> values
                    ? values.FirstOrDefault(v => v.Key == "{OriginalFormat}").Value as string
                    : null;
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
                    ? exception.GetProperty("values").EnumerateArray().Select(e => Text(e, "type")).ToArray()
                    : [];
                var template = root.TryGetProperty("logentry", out var logEntry) ? Text(logEntry, "message") : null;
                Events.Enqueue(new SentEvent(Text(root, "logger"), Text(root, "level"), template, exceptions));
            }
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) ? value.GetString() : null;
    }
}
