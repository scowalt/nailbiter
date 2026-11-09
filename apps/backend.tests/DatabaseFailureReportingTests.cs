using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using backend.Data;
using backend.Models;
using backend.Services;
using backend.Services.Highlights;
using backend.Services.Kalshi;
using backend.Services.PlayoffStatus;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Xunit;

namespace backend.tests;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class DatabaseFailureReportingTests
{
    private const string GameId = "database-failure-game";
    private const string EfCommandLogger = "Microsoft.EntityFrameworkCore.Database.Command";
    private const string LockTimeout = ";Options=-c lock_timeout=500";
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private const string MlbSchedule = """
        {"dates":[{"date":"2026-10-06","games":[{"gamePk":849819,"gameDate":"2026-10-06T22:00:00Z",
        "officialDate":"2026-10-06","season":"2026","doubleHeader":"N","gameNumber":1,
        "status":{"abstractGameState":"Final","detailedState":"Final","codedGameState":"F"},
        "teams":{"home":{"team":{"id":144}},"away":{"team":{"id":119}}}}]}]}
        """;
    private const string EspnSummary = """
        {"header":{"id":"database-failure-mlb","season":{"year":2026},"competitions":[{
        "id":"database-failure-mlb","date":"2026-10-06T22:00:00Z",
        "status":{"type":{"name":"STATUS_FINAL","state":"post","completed":true}},
        "competitors":[{"homeAway":"home","team":{"id":"15","abbreviation":"ATL"}},
        {"homeAway":"away","team":{"id":"19","abbreviation":"LAD"}}]}]}}
        """;

    [Fact]
    public async Task Request_query_failure_is_reported_once_with_its_sqlstate()
    {
        await using var database = await SyntheticDatabase.StartAsync();
        await using var oddsLock = await database.HoldLockAsync("LOCK TABLE odds_snapshots IN ACCESS EXCLUSIVE MODE");
        await using var app = new ReportingApp(database.ConnectionString + LockTimeout, worker: null);

        await Record.ExceptionAsync(() => app.Client.GetAsync($"/api/games/{GameId}/odds"));

        AssertReportedOnce(await app.SentEventsAfterFailureAsync(), PostgresErrorCodes.LockNotAvailable);
    }

    [Fact]
    public async Task Pro_evidence_write_failure_is_reported_once_with_its_sqlstate()
    {
        await using var database = await SyntheticDatabase.StartAsync();
        await using var rowLock = await database.HoldLockAsync($"SELECT 1 FROM games WHERE id = '{GameId}' FOR UPDATE");
        await using var app = new ReportingApp(database.ConnectionString + LockTimeout,
            worker: typeof(ProCompetitionRefreshBackgroundService));

        AssertReportedOnce(await app.SentEventsAfterFailureAsync(), PostgresErrorCodes.LockNotAvailable);
    }

    [Fact]
    public async Task Highlights_lifecycle_failure_is_reported_once_with_its_sqlstate()
    {
        await using var database = await SyntheticDatabase.StartAsync();
        await using var tableLock = await database.HoldLockAsync("LOCK TABLE highlights_operations IN ACCESS EXCLUSIVE MODE");
        await using var app = new ReportingApp(database.ConnectionString + LockTimeout, typeof(HighlightsBackgroundService));

        AssertReportedOnce(await app.SentEventsAfterFailureAsync(), PostgresErrorCodes.LockNotAvailable);
    }

    [Fact]
    public async Task Highlights_detail_failure_is_reported_once_and_the_page_still_renders()
    {
        await using var database = await SyntheticDatabase.StartAsync();
        await database.AddGameAsync(new Game
        {
            Id = "database-failure-final", Sport = "mlb", Status = "final", SeasonYear = 2026, SeasonType = 2,
            HomeTeam = "Home", AwayTeam = "Away", HomeTeamAbbreviation = "HOM", AwayTeamAbbreviation = "AWY",
            ScheduledStart = DateTime.UtcNow.AddDays(-1)
        });
        await using var tableLock = await database.HoldLockAsync("LOCK TABLE game_highlights IN ACCESS EXCLUSIVE MODE");
        await using var app = new ReportingApp(database.ConnectionString + LockTimeout, worker: null);

        using var response = await app.Client.GetAsync("/game/database-failure-final");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertReportedOnce(await app.SentEventsAfterFailureAsync(), PostgresErrorCodes.LockNotAvailable);
    }

    [Fact]
    public async Task Kalshi_ratings_failure_is_reported_once_with_its_sqlstate()
    {
        await using var database = await SyntheticDatabase.StartAsync();
        await using var tableLock = await database.HoldLockAsync("LOCK TABLE team_ratings IN ACCESS EXCLUSIVE MODE");
        var nhlEvent = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NhlSeeding", "qualification.json"));
        var kalshi = new Provider(uri => uri.AbsolutePath.EndsWith("/events/KXNHLPLAYOFF-27", StringComparison.Ordinal) ? nhlEvent
            : uri.AbsolutePath.EndsWith("/historical/markets", StringComparison.Ordinal) ? """{"markets":[],"cursor":""}""" : "{}");
        await using var app = new ReportingApp(database.ConnectionString + LockTimeout, typeof(KalshiPlayoffRefreshBackgroundService), services =>
        {
            PinClock(services);
            services.AddHttpClient<KalshiPlayoffRatingsService>().ConfigurePrimaryHttpMessageHandler(() => kalshi);
        });

        AssertReportedOnce(await app.SentEventsAfterFailureAsync(), PostgresErrorCodes.LockNotAvailable);
    }

    [Fact]
    public async Task Kalshi_college_teams_failure_is_reported_once_with_its_sqlstate()
    {
        await using var database = await SyntheticDatabase.StartAsync();
        await using var tableLock = await database.HoldLockAsync("LOCK TABLE college_teams IN ACCESS EXCLUSIVE MODE");
        var kalshi = new Provider(_ => "{}");
        await using var app = new ReportingApp(database.ConnectionString + LockTimeout, typeof(KalshiPlayoffRefreshBackgroundService), services =>
        {
            PinClock(services);
            services.AddHttpClient<KalshiPlayoffRatingsService>().ConfigurePrimaryHttpMessageHandler(() => kalshi);
        });

        AssertReportedOnce(await app.SentEventsAfterFailureAsync(), PostgresErrorCodes.LockNotAvailable);
    }

    [Fact]
    public async Task Kalshi_playoff_refresh_own_fault_is_reported_once_at_error()
    {
        await using var database = await SyntheticDatabase.StartAsync();
        await using var app = new ReportingApp(database.ConnectionString, typeof(KalshiPlayoffRefreshBackgroundService), services =>
        {
            PinClock(services);
            services.AddScoped<KalshiPlayoffRatingsService>(_ => throw new InvalidOperationException("Synthetic fault resolving the refresh"));
        });

        var reported = Assert.Single(await app.SentEventsAfterFailureAsync());
        Assert.Equal((typeof(KalshiPlayoffRefreshBackgroundService).FullName, "error"), (reported.Logger, reported.Level));
        Assert.Contains(reported.Exceptions, e => e.Type == typeof(InvalidOperationException).FullName);
    }

    [Fact]
    public async Task PlayoffStatus_model_commit_failure_is_reported_once_with_its_sqlstate()
    {
        await using var database = await SyntheticDatabase.StartAsync();
        await using var tableLock = await database.HoldLockAsync("LOCK TABLE team_ratings IN ACCESS EXCLUSIVE MODE");
        var published = Now.UtcDateTime.AddHours(-24);
        var playoffStatus = new Provider(uri => NflPlayoffPageTests.PlayoffStatusFixture(uri.AbsoluteUri, published, invalidBos: false));
        await using var app = new ReportingApp(database.ConnectionString + LockTimeout, typeof(PlayoffStatusNhlRefreshBackgroundService), services =>
        {
            PinClock(services);
            services.AddHttpClient(nameof(PlayoffStatusService)).ConfigurePrimaryHttpMessageHandler(() => playoffStatus);
            services.PostConfigure<PlayoffRatingsOptions>(options =>
            {
                options.NhlModelMode = "fallback";
                options.NhlModelRefreshEnabled = true;
                options.ApprovedSeasons = [2027];
            });
        });

        AssertReportedOnce(await app.SentEventsAfterFailureAsync(), PostgresErrorCodes.LockNotAvailable);
    }

    [Fact]
    public async Task MoneyPuck_collection_failure_is_reported_once_and_the_cycle_is_finalized()
    {
        await using var database = await SyntheticDatabase.StartAsync();
        await using var tableLock = await database.HoldLockAsync("LOCK TABLE team_ratings IN ACCESS EXCLUSIVE MODE");
        using var moneyPuck = new MoneyPuckQualificationHandler();
        await using var app = MoneyPuckApp(database, moneyPuck);

        AssertReportedOnce(await app.SentEventsAfterFailureAsync(), PostgresErrorCodes.LockNotAvailable);
        Assert.Equal(3, moneyPuck.Requests.Count);
        Assert.False(await database.ScalarAsync<bool>("SELECT active FROM moneypuck_refresh_state WHERE id = 1", until: active => !active));
    }

    [Fact]
    public async Task MoneyPuck_claim_failure_is_reported_once_without_provider_requests()
    {
        await using var database = await SyntheticDatabase.StartAsync();
        await using var tableLock = await database.HoldLockAsync("LOCK TABLE moneypuck_refresh_state IN ACCESS EXCLUSIVE MODE");
        using var moneyPuck = new MoneyPuckQualificationHandler();
        await using var app = MoneyPuckApp(database, moneyPuck);

        AssertReportedOnce(await app.SentEventsAfterFailureAsync(), PostgresErrorCodes.LockNotAvailable);
        Assert.Empty(moneyPuck.Requests);
    }

    [Fact]
    public async Task MoneyPuck_finalize_failure_is_reported_once_and_the_cycle_stays_blocked()
    {
        await using var database = await SyntheticDatabase.StartAsync();
        using var moneyPuck = new MoneyPuckQualificationHandler();
        IAsyncDisposable? rowLock = null;
        moneyPuck.Override = async (_, _) =>
        {
            rowLock ??= await database.HoldLockAsync("SELECT 1 FROM moneypuck_refresh_state WHERE id = 1 FOR UPDATE");
            return null;
        };
        try
        {
            await using var app = MoneyPuckApp(database, moneyPuck);

            AssertReportedOnce(await app.SentEventsAfterFailureAsync(), PostgresErrorCodes.LockNotAvailable);
            Assert.True(await database.ScalarAsync<bool>("SELECT active FROM moneypuck_refresh_state WHERE id = 1"));
        }
        finally
        {
            if (rowLock is not null) await rowLock.DisposeAsync();
        }
    }

    [Fact]
    public async Task MoneyPuck_own_fault_in_the_collection_is_reported_once_and_the_cycle_is_finalized()
    {
        await using var database = await SyntheticDatabase.StartAsync();
        using var moneyPuck = new MoneyPuckQualificationHandler
        {
            Override = (_, _) => throw new InvalidOperationException("Synthetic fault below the fetch")
        };
        await using var app = MoneyPuckApp(database, moneyPuck);

        AssertSentOnceAtError<MoneyPuckService, InvalidOperationException>(await app.SentEventsAfterFailureAsync());
        Assert.False(await database.ScalarAsync<bool>("SELECT active FROM moneypuck_refresh_state WHERE id = 1", until: active => !active));
    }

    [Fact]
    public async Task MoneyPuck_client_deadline_is_reported_once_at_error()
    {
        await using var database = await SyntheticDatabase.StartAsync();
        using var moneyPuck = new MoneyPuckQualificationHandler
        {
            Override = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return null;
            }
        };
        await using var app = new ReportingApp(database.ConnectionString, typeof(MoneyPuckRefreshBackgroundService), services =>
        {
            PinClock(services);
            services.AddHttpClient<MoneyPuckService>(client => client.Timeout = TimeSpan.FromMilliseconds(200))
                .ConfigurePrimaryHttpMessageHandler(() => moneyPuck);
        });

        AssertSentOnceAtError<MoneyPuckService, TaskCanceledException>(await app.SentEventsAfterFailureAsync());
    }

    [Fact]
    public async Task MoneyPuck_missing_answer_is_a_warning_without_an_event()
    {
        await using var database = await SyntheticDatabase.StartAsync();
        using var moneyPuck = new MoneyPuckQualificationHandler
        {
            Override = (_, _) => Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(HttpStatusCode.NotFound))
        };
        await using var app = MoneyPuckApp(database, moneyPuck);

        Assert.Empty(await app.SentEventsAfterFailureAsync());
        Assert.Single(moneyPuck.Requests);
    }

    [Fact]
    public async Task MoneyPuck_background_own_fault_is_reported_once_at_error()
    {
        await using var database = await SyntheticDatabase.StartAsync();
        await using var app = new ReportingApp(database.ConnectionString, typeof(MoneyPuckRefreshBackgroundService), services =>
        {
            PinClock(services);
            services.RemoveAll<MoneyPuckService>();
            services.AddTransient<MoneyPuckService>(_ => throw new InvalidOperationException("Synthetic fault resolving the collection"));
        });

        AssertSentOnceAtError<MoneyPuckRefreshBackgroundService, InvalidOperationException>(await app.SentEventsAfterFailureAsync());
    }

    [Fact]
    public async Task MoneyPuck_missing_admission_row_is_reported_once_without_provider_requests()
    {
        await using var database = await SyntheticDatabase.StartAsync();
        await database.ScalarAsync<int>("""
            CREATE FUNCTION skip_insert() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NULL; END $$;
            CREATE TRIGGER skip_insert BEFORE INSERT ON moneypuck_refresh_state FOR EACH ROW EXECUTE FUNCTION skip_insert();
            SELECT 1
            """);
        using var moneyPuck = new MoneyPuckQualificationHandler();
        await using var app = MoneyPuckApp(database, moneyPuck);

        AssertSentOnceAtError<MoneyPuckRefreshGate, InvalidOperationException>(await app.SentEventsAfterFailureAsync());
        Assert.Empty(moneyPuck.Requests);
    }

    [Fact]
    public async Task MoneyPuck_finalize_deadline_is_reported_once_and_the_cycle_stays_blocked()
    {
        await using var database = await SyntheticDatabase.StartAsync();
        using var moneyPuck = new MoneyPuckQualificationHandler();
        IAsyncDisposable? rowLock = null;
        moneyPuck.Override = async (_, _) =>
        {
            rowLock ??= await database.HoldLockAsync("SELECT 1 FROM moneypuck_refresh_state WHERE id = 1 FOR UPDATE");
            return null;
        };
        try
        {
            await using var app = new ReportingApp(database.ConnectionString, typeof(MoneyPuckRefreshBackgroundService), services =>
            {
                PinClock(services);
                services.AddHttpClient<MoneyPuckService>().ConfigurePrimaryHttpMessageHandler(() => moneyPuck);
            });

            AssertSentOnceAtError<MoneyPuckRefreshGate, OperationCanceledException>(await app.SentEventsAfterFailureAsync());
            Assert.True(await database.ScalarAsync<bool>("SELECT active FROM moneypuck_refresh_state WHERE id = 1"));
        }
        finally
        {
            if (rowLock is not null) await rowLock.DisposeAsync();
        }
    }

    [Fact]
    public async Task Highlights_operator_charge_failure_is_reported_as_an_error_and_the_attach_is_refused()
    {
        await using var database = await SyntheticDatabase.StartAsync();
        await database.AddGameAsync(new Game
        {
            Id = "database-failure-mlb", Sport = "mlb", Status = "final", SeasonYear = 2026,
            HomeTeam = "Atlanta Braves", AwayTeam = "Los Angeles Dodgers", HomeTeamAbbreviation = "ATL", AwayTeamAbbreviation = "LAD",
            HomeTeamId = "15", AwayTeamId = "19", ScheduledStart = new DateTime(2026, 10, 6, 22, 0, 0, DateTimeKind.Utc),
            CompletionConfirmedAt = new DateTime(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc),
            HighlightsDiscoveryExpiresAt = new DateTime(2026, 10, 10, 3, 0, 0, DateTimeKind.Utc)
        });
        var mlb = new Provider(uri => uri.Host == "statsapi.mlb.com" ? MlbSchedule : EspnSummary);
        await using var youtube = new RowLockedDuringChannelRequest(database);
        var transport = new SentTransport();

        var exitCode = await ProgramEntryPoint.RunAsync(
            [
                "--environment=Production",
                $"--ConnectionStrings:DefaultConnection={database.ConnectionString}{LockTimeout}",
                "--Sentry:Dsn=https://public@sentry.invalid/1",
                "--Highlights:YouTube:Enabled=true",
                "--Highlights:YouTube:ApiKey=sentinel-not-live",
                "--highlights", "attach", "database-failure-mlb", "manualVideo", "--apply"
            ],
            services =>
            {
                services.PostConfigure<SentryAspNetCoreOptions>(options => options.Transport = transport);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 3, 0, 0, TimeSpan.Zero)));
                services.AddHttpClient(MlbHighlightsIdentityClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => mlb);
                services.AddHttpClient(YouTubeHighlightsClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => youtube);
                services.ConfigureDbContext<ExcitementDbContext>(options => options.AddInterceptors(youtube));
            });

        Assert.Equal(new[] { "channels" }, youtube.Requests);
        Assert.Equal(2, exitCode);
        Assert.Equal(nameof(YouTubeOutcome.InvalidResponse),
            await database.ScalarAsync<string>("SELECT last_reason FROM highlights_operations WHERE id = 1"));
        Assert.False(SentrySdk.IsEnabled);
        AssertReportedOnce(transport.Events.ToArray(), PostgresErrorCodes.LockNotAvailable);
    }

    private static ReportingApp MoneyPuckApp(SyntheticDatabase database, MoneyPuckQualificationHandler moneyPuck) =>
        new(database.ConnectionString + LockTimeout, typeof(MoneyPuckRefreshBackgroundService),
            services =>
            {
                PinClock(services);
                services.AddHttpClient<MoneyPuckService>().ConfigurePrimaryHttpMessageHandler(() => moneyPuck);
            });

    private static void PinClock(IServiceCollection services)
    {
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
    }

    private static void AssertSentOnceAtError<TLogger, TException>(IReadOnlyCollection<SentEvent> sent)
    {
        var reported = Assert.Single(sent);
        Assert.Equal((typeof(TLogger).FullName, "error"), (reported.Logger, reported.Level));
        Assert.Contains(reported.Exceptions, e => e.Type == typeof(TException).FullName);
    }

    private static void AssertReportedOnce(IReadOnlyCollection<SentEvent> sent, string sqlState)
    {
        Assert.DoesNotContain(sent, e => e.Logger == EfCommandLogger);
        var reported = Assert.Single(sent);
        Assert.Equal("error", reported.Level);
        var failure = Assert.Single(reported.Exceptions, e => e.Type == typeof(PostgresException).FullName);
        Assert.StartsWith(sqlState + ":", failure.Value);
        Assert.Contains(EfCommandLogger, reported.BreadcrumbCategories);
    }

    private sealed class SyntheticDatabase : IAsyncDisposable
    {
        private readonly Testcontainers.PostgreSql.PostgreSqlContainer container = IsolatedPostgreSql.CreateContainer();

        public string ConnectionString => container.GetConnectionString();

        public static async Task<SyntheticDatabase> StartAsync()
        {
            var database = new SyntheticDatabase();
            await database.container.StartAsync();
            var options = new DbContextOptionsBuilder<ExcitementDbContext>().UseNpgsql(database.ConnectionString).Options;
            await using var db = new ExcitementDbContext(options);
            await db.Database.EnsureCreatedAsync();
            db.Games.Add(new Game
            {
                Id = GameId, Sport = "nba", Status = "scheduled", SeasonYear = 2027, SeasonType = 2,
                HomeTeam = "Home", AwayTeam = "Away", HomeTeamAbbreviation = "HOM", AwayTeamAbbreviation = "AWY",
                ScheduledStart = DateTime.UtcNow.AddDays(1)
            });
            await db.SaveChangesAsync();
            return database;
        }

        public async Task AddGameAsync(Game game)
        {
            var options = new DbContextOptionsBuilder<ExcitementDbContext>().UseNpgsql(ConnectionString).Options;
            await using var db = new ExcitementDbContext(options);
            db.Games.Add(game);
            await db.SaveChangesAsync();
        }

        public async Task<T> ScalarAsync<T>(string sql, Func<T, bool>? until = null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                await using var connection = new NpgsqlConnection(ConnectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(sql, connection);
                var value = (T)(await command.ExecuteScalarAsync())!;
                if (until?.Invoke(value) != false || deadline.IsCancellationRequested) return value;
                await Task.Delay(50, CancellationToken.None);
            }
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

        public ReportingApp(string connectionString, Type? worker, Action<IServiceCollection>? configure = null)
        {
            app = entry.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
                    services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
                    services.AddDbContext<ExcitementDbContext>(options => options.UseNpgsql(connectionString));
                    foreach (var other in services.Where(s => s.ServiceType == typeof(IHostedService)
                        && s.ImplementationType?.Namespace?.StartsWith("backend", StringComparison.Ordinal) == true).ToArray())
                        services.Remove(other);
                    if (worker is not null) services.AddSingleton(typeof(IHostedService), worker);
                    services.AddHttpClient<ProCompetitionEvidenceService>().ConfigurePrimaryHttpMessageHandler(() => new Provider(_ => "{}"));
                    services.PostConfigure<SentryAspNetCoreOptions>(options =>
                    {
                        options.Dsn = "https://public@sentry.invalid/1";
                        options.Transport = transport;
                    });
                    configure?.Invoke(services);
                });
            });
            Client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        }

        public HttpClient Client { get; }

        public async Task<IReadOnlyCollection<SentEvent>> SentEventsAfterFailureAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!transport.Events.Any(e => e.Exceptions.Length > 0) && !deadline.IsCancellationRequested)
                await Task.Delay(50, CancellationToken.None);
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
            return transport.Events.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            await entry.DisposeAsync();
        }
    }

    private static class ProgramEntryPoint
    {
        private static readonly AsyncLocal<object?> CurrentRun = new();

        public static async Task<int> RunAsync(string[] args, Action<IServiceCollection> configure)
        {
            var run = new object();
            IHost? host = null;
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(listener =>
            {
                if (listener.Name != "Microsoft.Extensions.Hosting" || CurrentRun.Value != run) return;
                listener.Subscribe(new Observer<KeyValuePair<string, object?>>(e =>
                {
                    if (CurrentRun.Value != run) return;
                    if (e.Key == "HostBuilding") ((IHostBuilder)e.Value!).ConfigureServices(configure);
                    if (e.Key == "HostBuilt") host = (IHost)e.Value!;
                }));
            }));
            var thread = new Thread(() =>
            {
                try
                {
                    CurrentRun.Value = run;
                    typeof(Program).Assembly.EntryPoint!.Invoke(null, [args]);
                    completed.SetResult();
                }
                catch (Exception e)
                {
                    completed.SetException(e);
                }
            }) { IsBackground = true };
            using (ExecutionContext.SuppressFlow()) thread.Start();
            try
            {
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(60));
                await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
                return Environment.ExitCode;
            }
            finally
            {
                Environment.ExitCode = 0;
                SentrySdk.Close();
                if (host is IAsyncDisposable disposable) await disposable.DisposeAsync();
            }
        }

        private sealed class Observer<T>(Action<T> next) : IObserver<T>
        {
            public void OnNext(T value) => next(value);
            public void OnError(Exception error) { }
            public void OnCompleted() { }
        }
    }

    private sealed class Provider(Func<Uri, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request.RequestUri!)) });
    }

    private sealed class RowLockedDuringChannelRequest(SyntheticDatabase database) : HttpMessageHandler, IDbCommandInterceptor, IAsyncDisposable
    {
        private IAsyncDisposable? rowLock;

        public ConcurrentQueue<string> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!.Segments[^1]);
            rowLock ??= await database.HoldLockAsync("SELECT 1 FROM highlights_operations WHERE id = 1 FOR UPDATE");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"items":[{"id":"UCoLrcjPV5PbUrUyXq5mjc_A","contentDetails":{"relatedPlaylists":{"uploads":"fixture-uploads"}}}]}""")
            };
        }

        public async Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default) =>
            await ReleaseAsync();

        private async Task ReleaseAsync()
        {
            if (Interlocked.Exchange(ref rowLock, null) is { } held) await held.DisposeAsync();
        }

        async ValueTask IAsyncDisposable.DisposeAsync() => await ReleaseAsync();
    }

    private sealed record SentEvent(string? Logger, string? Level, (string Type, string Value)[] Exceptions, string?[] BreadcrumbCategories);

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
                    ? exception.GetProperty("values").EnumerateArray()
                        .Select(e => (e.GetProperty("type").GetString()!, e.TryGetProperty("value", out var v) ? v.GetString()! : "")).ToArray()
                    : [];
                var breadcrumbs = root.TryGetProperty("breadcrumbs", out var crumbs)
                    ? crumbs.EnumerateArray().Select(b => Text(b, "category")).ToArray()
                    : [];
                Events.Enqueue(new SentEvent(Text(root, "logger"), Text(root, "level"), exceptions, breadcrumbs));
            }
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) ? value.GetString() : null;
    }
}
