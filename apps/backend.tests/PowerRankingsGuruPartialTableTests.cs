using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using backend.Data;
using backend.Models;
using backend.Services.PowerRankingsGuru;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Testcontainers.PostgreSql;
using Xunit;

namespace backend.tests;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class PowerRankingsGuruPartialTableTests
{
    private const int Season = 2026;
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 11, 0, 0, TimeSpan.Zero);
    private static readonly string Category = typeof(PowerRankingsGuruService).FullName!;
    private const string WrongCount = "PowerRankingsGuru {Sport} rankings rejected: {Parsed} rows parsed for {Expected} teams";
    private const string Duplicates = "PowerRankingsGuru {Sport} rankings rejected: {Parsed} rows name only {Distinct} distinct teams";

    private static readonly string[] Nfl =
    [
        "ARI", "ATL", "BAL", "BUF", "CAR", "CHI", "CIN", "CLE", "DAL", "DEN", "DET", "GB", "HOU", "IND", "JAX", "KC",
        "LV", "LAC", "LAR", "MIA", "MIN", "NE", "NO", "NYG", "NYJ", "PHI", "PIT", "SF", "SEA", "TB", "TEN", "WSH",
    ];

    private static readonly Dictionary<string, (int Rank, string Team)[]> PartialTables = new()
    {
        ["one row"] = [(1, "KC")],
        ["one team short"] = Nfl.Take(31).Select((team, i) => (i + 1, team)).ToArray(),
        ["a duplicate team filling the count"] = Nfl.Take(31).Select((team, i) => (i + 1, team)).Append((32, "ARI")).ToArray(),
        ["one team too many"] = Nfl.Select((team, i) => (i + 1, team)).Append((32, "XXX")).ToArray(),
        ["a duplicate team over the count"] = Nfl.Select((team, i) => (i + 1, team)).Append((32, "ARI")).ToArray(),
    };

    [Theory]
    [InlineData("one row", WrongCount)]
    [InlineData("one team short", WrongCount)]
    [InlineData("a duplicate team filling the count", Duplicates)]
    [InlineData("one team too many", WrongCount)]
    [InlineData("a duplicate team over the count", Duplicates)]
    public async Task Partial_table_is_rejected_at_warning_without_an_event_and_stores_none_of_its_ranks(string table, string reason)
    {
        await using var database = await StartDatabaseAsync();
        await SeedEarlierBatchAsync(database);
        await using var app = new GuruApp(Page(PartialTables[table]), database.GetConnectionString());

        Assert.Equal(0, await app.RefreshNflAsync());

        var rejected = Assert.Single(app.Logs, l => l.Category == Category && l.Level >= LogLevel.Warning);
        Assert.Equal((LogLevel.Warning, reason), (rejected.Level, rejected.Template));
        Assert.Empty(await app.SentEventsAsync());
        Assert.All(await RanksAsync(database), rank => Assert.Null(rank.Value));
    }

    [Fact]
    public async Task Complete_table_replaces_the_earlier_batch_without_a_warning()
    {
        await using var database = await StartDatabaseAsync();
        await SeedEarlierBatchAsync(database);
        var page = Nfl.Reverse().Select((team, i) => (i + 1, team)).ToArray();
        await using var app = new GuruApp(Page(page), database.GetConnectionString());

        Assert.Equal(Nfl.Length, await app.RefreshNflAsync());

        Assert.DoesNotContain(app.Logs, l => l.Category == Category && l.Level >= LogLevel.Warning);
        Assert.Empty(await app.SentEventsAsync());
        Assert.Equal(page.ToDictionary(r => r.Item2, r => (int?)r.Item1), await RanksAsync(database));
    }

    private static string Page(IEnumerable<(int Rank, string Team)> rows) =>
        $"""
        <html><body><h1 class="content-title">{Season} Composite NFL Power Rankings</h1>
        <table class="rankings-table"><tbody>
        {string.Concat(rows.Select(r => $"<tr><td class=\"rank-v2\">{r.Rank}</td><td><span class=\"full-name\">{r.Team}</span><span class=\"abbrv\">{r.Team}</span></td></tr>"))}
        </tbody></table></body></html>
        """;

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = IsolatedPostgreSql.CreateContainer();
        await database.StartAsync();
        await using var db = Open(database);
        await TestSchema.CreateAsync(db.Database);
        return database;
    }

    private static ExcitementDbContext Open(PostgreSqlContainer database) =>
        new(new DbContextOptionsBuilder<ExcitementDbContext>().UseNpgsql(database.GetConnectionString()).Options);

    private static async Task SeedEarlierBatchAsync(PostgreSqlContainer database)
    {
        await using var db = Open(database);
        db.TeamRatings.AddRange(Nfl.Select((team, i) => new TeamRating
        {
            Sport = "nfl", TeamAbbreviation = team, TeamName = team, Source = "powerrankingsguru", Season = Season,
            SourceSeason = Season, PowerRank = i + 1, FetchedAt = Now.AddDays(-1).UtcDateTime,
        }));
        await db.SaveChangesAsync();
    }

    private static async Task<Dictionary<string, int?>> RanksAsync(PostgreSqlContainer database)
    {
        await using var db = Open(database);
        return await db.TeamRatings.Where(r => r.Sport == "nfl" && r.Source == "powerrankingsguru" && r.Season == Season)
            .ToDictionaryAsync(r => r.TeamAbbreviation, r => r.PowerRank);
    }

    private sealed record SentEvent(string? Logger, string? Level);

    private sealed record LoggedEntry(string Category, LogLevel Level, string? Template);

    private sealed class GuruApp : IAsyncDisposable
    {
        private readonly AppFactory entry = new();
        private readonly WebApplicationFactory<Program> app;
        private readonly SentTransport transport = new();
        private readonly LogRecorder logs = new();
        private readonly FixedPage provider;

        public GuruApp(string html, string connectionString)
        {
            provider = new FixedPage(html);
            app = entry.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.ConfigureServices(services =>
                {
                    foreach (var worker in services.Where(s => s.ServiceType == typeof(IHostedService)
                        && s.ImplementationType?.Namespace?.StartsWith("backend", StringComparison.Ordinal) == true).ToArray())
                        services.Remove(worker);
                    services.ConfigureAll<HttpClientFactoryOptions>(options =>
                        options.HttpMessageHandlerBuilderActions.Add(handlers => handlers.PrimaryHandler = provider));
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
                    services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
                    services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
                    services.AddDbContext<ExcitementDbContext>(db => db.UseNpgsql(connectionString));
                    services.AddSingleton<ILoggerProvider>(logs);
                    services.PostConfigure<SentryAspNetCoreOptions>(options =>
                    {
                        options.Dsn = "https://public@sentry.invalid/1";
                        options.Transport = transport;
                    });
                });
            });
        }

        public IEnumerable<LoggedEntry> Logs => logs.Entries;

        public Task<int> RefreshNflAsync() =>
            app.Services.GetRequiredService<PowerRankingsGuruService>().RefreshNflRatingsAsync(CancellationToken.None);

        public async Task<IReadOnlyCollection<SentEvent>> SentEventsAsync()
        {
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
            return transport.Events.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            await entry.DisposeAsync();
            provider.Dispose();
        }
    }

    private sealed class FixedPage(string html) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
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
                entries.Enqueue(new LoggedEntry(category, logLevel,
                    (state as IReadOnlyList<KeyValuePair<string, object?>>)?.FirstOrDefault(p => p.Key == "{OriginalFormat}").Value as string));
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
                Events.Enqueue(new SentEvent(Text(root, "logger"), Text(root, "level")));
            }
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) ? value.GetString() : null;
    }
}
