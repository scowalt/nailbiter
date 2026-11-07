using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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

    [Fact]
    public async Task Request_query_failure_is_reported_once_with_its_sqlstate()
    {
        await using var database = await SyntheticDatabase.StartAsync();
        await using var oddsLock = await database.HoldLockAsync("LOCK TABLE odds_snapshots IN ACCESS EXCLUSIVE MODE");
        await using var app = new ReportingApp(database.ConnectionString + ";Options=-c lock_timeout=500", worker: null);

        await Record.ExceptionAsync(() => app.Client.GetAsync($"/api/games/{GameId}/odds"));

        AssertReportedOnce(await app.SentEventsAfterFailureAsync(), PostgresErrorCodes.LockNotAvailable);
    }

    [Fact]
    public async Task Pro_evidence_write_failure_is_reported_once_with_its_sqlstate()
    {
        await using var database = await SyntheticDatabase.StartAsync();
        await using var rowLock = await database.HoldLockAsync($"SELECT 1 FROM games WHERE id = '{GameId}' FOR UPDATE");
        await using var app = new ReportingApp(database.ConnectionString + ";Options=-c lock_timeout=500",
            worker: typeof(ProCompetitionRefreshBackgroundService));

        AssertReportedOnce(await app.SentEventsAfterFailureAsync(), PostgresErrorCodes.LockNotAvailable);
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

        public ReportingApp(string connectionString, Type? worker)
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
                        && s.ImplementationType?.Namespace?.StartsWith("backend", StringComparison.Ordinal) == true
                        && s.ImplementationType != worker).ToArray())
                        services.Remove(other);
                    services.AddHttpClient<ProCompetitionEvidenceService>().ConfigurePrimaryHttpMessageHandler(() => new EmptyEspn());
                    services.PostConfigure<SentryAspNetCoreOptions>(options =>
                    {
                        options.Dsn = "https://public@sentry.invalid/1";
                        options.Transport = transport;
                    });
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

    private sealed class EmptyEspn : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
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
