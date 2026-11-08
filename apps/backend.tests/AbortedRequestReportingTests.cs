using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using backend.Data;
using backend.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Testcontainers.PostgreSql;
using Xunit;

namespace backend.tests;

[CollectionDefinition(nameof(SentryGlobalHubCollection), DisableParallelization = true)]
public sealed class SentryGlobalHubCollection;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class AbortedRequestReportingTests
{
    private const string GameId = "aborted-request-game";

    [Theory]
    [InlineData("/api/games/" + GameId + "/odds")]
    [InlineData("/")]
    public async Task Client_abort_during_a_blocked_query_is_not_reported_as_an_error(string path)
    {
        await using var app = await ReportingApp.StartAsync(connectionOptions: "");
        await using var oddsLock = await app.LockAsync("odds_snapshots", "ACCESS EXCLUSIVE");

        using var abort = new CancellationTokenSource();
        var request = app.Client.GetAsync(path, abort.Token);
        await oddsLock.WaitForBlockedQueryAsync();
        abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        var status = await app.RequestCompleted(path);

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, status);
        Assert.DoesNotContain(app.Events, e => e.Path == path && e.IsQueryCancellation);
    }

    [Fact]
    public async Task Client_abort_during_a_pro_hype_save_sends_nothing()
    {
        const string path = "/nba";
        await using var app = await ReportingApp.StartAsync(connectionOptions: "");
        await app.AddGameAsync(new Game
        {
            Id = "aborted-save-game", Sport = "nba", Status = "scheduled", SeasonYear = 2027, SeasonType = 2,
            HomeTeam = "Home", AwayTeam = "Away", HomeTeamAbbreviation = "HOM", AwayTeamAbbreviation = "AWY",
            ScheduledStart = new DateTime(2027, 1, 15, 19, 0, 0, DateTimeKind.Utc)
        });
        await using var snapshotLock = await app.LockAsync("pro_hype_snapshots", "SHARE");

        using var abort = new CancellationTokenSource();
        var request = app.Client.GetAsync(path + "?date=2027-01-15", abort.Token);
        await snapshotLock.WaitForBlockedQueryAsync();
        abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(StatusCodes.Status499ClientClosedRequest, await app.RequestCompleted(path));
        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(app.Sent, e => e.Path == path);
    }

    [Fact]
    public async Task Cancellation_while_the_client_is_connected_is_still_reported_as_an_unhandled_error()
    {
        const string path = "/api/games/" + GameId + "/odds";
        await using var app = await ReportingApp.StartAsync(connectionOptions: "", new CancelOddsQueries());

        await Record.ExceptionAsync(() => app.Client.GetAsync(path));
        await app.RequestCompleted(path);

        var reported = Assert.Single(app.Events, e => e.Path == path && e.IsQueryCancellation);
        Assert.False(reported.Handled);
        Assert.IsType<OperationCanceledException>(reported.Exception);
    }

    [Fact]
    public async Task Server_statement_timeout_is_still_reported_as_an_unhandled_error()
    {
        const string path = "/api/games/" + GameId + "/odds";
        await using var app = await ReportingApp.StartAsync(connectionOptions: ";Options=-c statement_timeout=1000");
        await using var oddsLock = await app.LockAsync("odds_snapshots", "ACCESS EXCLUSIVE");

        await Record.ExceptionAsync(() => app.Client.GetAsync(path));
        await app.RequestCompleted(path);

        var reported = Assert.Single(app.Events, e => e.Path == path && e.IsQueryCancellation);
        Assert.False(reported.Handled);
        var timeout = Assert.IsType<PostgresException>(reported.Exception);
        Assert.Equal(PostgresErrorCodes.QueryCanceled, timeout.SqlState);
    }

    private sealed record SentEvent(string? Path, string? Logger, string? Message);

    private sealed record ReportedEvent(string? Path, Exception? Exception, bool Handled)
    {
        public bool IsQueryCancellation => Chain().Any(e =>
            e is OperationCanceledException || e is PostgresException { SqlState: PostgresErrorCodes.QueryCanceled });

        private IEnumerable<Exception> Chain()
        {
            for (var e = Exception; e is not null; e = e.InnerException) yield return e;
        }
    }

    private sealed class ReportingApp : IAsyncDisposable
    {
        private readonly PostgreSqlContainer database;
        private readonly AppFactory entry = new();
        private readonly WebApplicationFactory<Program> app;
        private readonly SentryRecorder recorder = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<int>> completed = new();

        private ReportingApp(PostgreSqlContainer database, string connectionString, IInterceptor[] interceptors)
        {
            this.database = database;
            app = entry.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
                    services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
                    services.AddDbContext<ExcitementDbContext>(options => options.UseNpgsql(connectionString).AddInterceptors(interceptors));
                    services.PostConfigure<SentryAspNetCoreOptions>(options =>
                    {
                        options.Dsn = "https://public@sentry.invalid/1";
                        options.Transport = recorder;
                        options.AddEventProcessor(recorder);
                    });
                    services.Insert(0, ServiceDescriptor.Singleton<IStartupFilter>(new CompletionSignalOutsideSentry(completed)));
                });
            });
            Client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        }

        public HttpClient Client { get; }
        public IEnumerable<ReportedEvent> Events => recorder.Events;
        public IEnumerable<SentEvent> Sent => recorder.Sent;

        public static async Task<ReportingApp> StartAsync(string connectionOptions, params IInterceptor[] interceptors)
        {
            var database = IsolatedPostgreSql.CreateContainer();
            await database.StartAsync();
            var started = new ReportingApp(database, database.GetConnectionString() + connectionOptions, interceptors);
            using var scope = started.app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Games.Add(new Game
            {
                Id = GameId, Sport = "nhl", Status = "in_progress", SeasonYear = 2027, SeasonType = 2,
                HomeTeam = "Home", AwayTeam = "Away", HomeTeamAbbreviation = "HOM", AwayTeamAbbreviation = "AWY",
                ScheduledStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
            await db.SaveChangesAsync();
            return started;
        }

        public async Task AddGameAsync(Game game)
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            db.Games.Add(game);
            await db.SaveChangesAsync();
        }

        public async Task<TableLock> LockAsync(string table, string mode)
        {
            var connection = new NpgsqlConnection(database.GetConnectionString());
            await connection.OpenAsync();
            var transaction = await connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand($"LOCK TABLE {table} IN {mode} MODE", connection, transaction);
            await command.ExecuteNonQueryAsync();
            return new TableLock(database.GetConnectionString(), table, connection, transaction);
        }

        public Task<int> RequestCompleted(string path) =>
            completed.GetOrAdd(path, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(TimeSpan.FromSeconds(30));

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            await entry.DisposeAsync();
            await database.DisposeAsync();
        }
    }

    private sealed class TableLock(string connectionString, string table, NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async Task WaitForBlockedQueryAsync()
        {
            await using var observer = new NpgsqlConnection(connectionString);
            await observer.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query ILIKE '%{table}%'",
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

    private sealed class CancelOddsQueries : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            command.CommandText.Contains("odds_snapshots")
                ? throw new OperationCanceledException("Synthetic server-side cancellation")
                : base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    private sealed class CompletionSignalOutsideSentry(ConcurrentDictionary<string, TaskCompletionSource<int>> completed) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, inner) =>
            {
                try { await inner(context); }
                finally
                {
                    completed.GetOrAdd(context.Request.Path, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(context.Response.StatusCode);
                }
            });
            next(app);
        };
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
                var url = root.TryGetProperty("request", out var request) && request.TryGetProperty("url", out var u) ? u.GetString() : null;
                var message = root.TryGetProperty("logentry", out var entry) && entry.TryGetProperty("message", out var m) ? m.GetString() : null;
                var logger = root.TryGetProperty("logger", out var l) ? l.GetString() : null;
                Sent.Enqueue(new SentEvent(url is null ? null : new Uri(url).AbsolutePath, logger, message));
            }
        }

        public SentryEvent? Process(SentryEvent sentryEvent)
        {
            var path = sentryEvent.Request.Url is { } url ? new Uri(url).AbsolutePath : null;
            var handled = sentryEvent.SentryExceptions?.All(e => e.Mechanism?.Handled != false) ?? true;
            Events.Enqueue(new ReportedEvent(path, sentryEvent.Exception, handled));
            return sentryEvent;
        }
    }
}
