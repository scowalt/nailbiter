using System.Collections.Concurrent;
using System.Data.Common;
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
        await using var oddsLock = await app.LockOddsSnapshotsAsync();

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
    public async Task Server_statement_timeout_is_still_reported_as_an_unhandled_error()
    {
        const string path = "/api/games/" + GameId + "/odds";
        await using var app = await ReportingApp.StartAsync(connectionOptions: ";Options=-c statement_timeout=1000");
        await using var oddsLock = await app.LockOddsSnapshotsAsync();

        await Record.ExceptionAsync(() => app.Client.GetAsync(path));
        await app.RequestCompleted(path);

        var reported = Assert.Single(app.Events, e => e.Path == path && e.IsQueryCancellation);
        Assert.False(reported.Handled);
        var timeout = Assert.IsType<PostgresException>(reported.Exception);
        Assert.Equal(PostgresErrorCodes.QueryCanceled, timeout.SqlState);
    }

    [Fact]
    public async Task Cancellation_thrown_while_the_client_is_connected_is_still_reported_as_an_unhandled_error()
    {
        const string path = "/api/games/" + GameId + "/odds";
        await using var app = await ReportingApp.StartAsync(connectionOptions: "");
        app.CancelOddsQueries();

        await Record.ExceptionAsync(() => app.Client.GetAsync(path));
        var status = await app.RequestCompleted(path);

        var reported = Assert.Single(app.Events, e => e.Path == path && e.IsQueryCancellation);
        Assert.False(reported.Handled);
        Assert.IsAssignableFrom<OperationCanceledException>(reported.Exception);
        Assert.NotEqual(StatusCodes.Status499ClientClosedRequest, status);
    }

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
        private readonly OddsQueryCanceller canceller = new();

        private ReportingApp(PostgreSqlContainer database, string connectionString)
        {
            this.database = database;
            app = entry.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
                    services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
                    services.AddDbContext<ExcitementDbContext>(options => options.UseNpgsql(connectionString).AddInterceptors(canceller));
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

        public static async Task<ReportingApp> StartAsync(string connectionOptions)
        {
            var database = IsolatedPostgreSql.CreateContainer();
            await database.StartAsync();
            var started = new ReportingApp(database, database.GetConnectionString() + connectionOptions);
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

        public async Task<OddsLock> LockOddsSnapshotsAsync()
        {
            var connection = new NpgsqlConnection(database.GetConnectionString());
            await connection.OpenAsync();
            var transaction = await connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand("LOCK TABLE odds_snapshots IN ACCESS EXCLUSIVE MODE", connection, transaction);
            await command.ExecuteNonQueryAsync();
            return new OddsLock(database.GetConnectionString(), connection, transaction);
        }

        public void CancelOddsQueries() => canceller.Armed = true;

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

    private sealed class OddsLock(string connectionString, NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async Task WaitForBlockedQueryAsync()
        {
            await using var observer = new NpgsqlConnection(connectionString);
            await observer.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query ILIKE '%odds_snapshots%'",
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

    private sealed class OddsQueryCanceller : DbCommandInterceptor
    {
        public volatile bool Armed;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken)
        {
            if (Armed && command.CommandText.Contains("odds_snapshots"))
                throw new OperationCanceledException("Cancelled by the server while the client was connected");
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class CompletionSignalOutsideSentry(ConcurrentDictionary<string, TaskCompletionSource<int>> completed) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, inner) =>
            {
                var status = StatusCodes.Status500InternalServerError;
                try
                {
                    await inner(context);
                    status = context.Response.StatusCode;
                }
                finally
                {
                    completed.GetOrAdd(context.Request.Path, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(status);
                }
            });
            next(app);
        };
    }

    private sealed class SentryRecorder : ITransport, ISentryEventProcessor
    {
        public ConcurrentQueue<ReportedEvent> Events { get; } = new();

        public Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public SentryEvent? Process(SentryEvent sentryEvent)
        {
            var path = sentryEvent.Request.Url is { } url ? new Uri(url).AbsolutePath : null;
            var handled = sentryEvent.SentryExceptions?.All(e => e.Mechanism?.Handled != false) ?? true;
            Events.Enqueue(new ReportedEvent(path, sentryEvent.Exception, handled));
            return sentryEvent;
        }
    }
}
