using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using backend.Data;
using backend.Services.Espn;
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
using Npgsql;
using Polly.Timeout;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Xunit;

namespace backend.tests;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class EspnStandingsReportingTests
{
    private const int StandardAttempts = 4;
    private const string Template = "Failed to refresh {Sport} standings from ESPN";
    private const string Standings = """
        {"children":[{"name":"American Football Conference","children":[{"name":"East","standings":{"entries":[
        {"team":{"id":"9001","displayName":"Synthetic Harbor","abbreviation":"SYH"},"stats":[{"name":"wins","value":7,"displayValue":"7"}]}]}}]}]}
        """;

    [Fact]
    public async Task Caller_cancellation_mid_refresh_propagates_without_an_event()
    {
        await using var app = new StandingsApp(Hang);
        using var caller = new CancellationTokenSource();

        var refresh = app.Standings.RefreshNflStandingsAsync(caller.Token);
        await app.Provider.FirstRequest.WaitAsync(TimeSpan.FromSeconds(10));
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Provider_timeout_after_retries_is_sent_once_at_error()
    {
        await using var app = new StandingsApp(Hang, services => services.ConfigureAll<HttpStandardResilienceOptions>(options =>
            options.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(50)));

        Assert.Equal(0, await app.Standings.RefreshNflStandingsAsync(CancellationToken.None));

        Assert.Equal(StandardAttempts, app.Provider.Requests);
        AssertSentOnceAtError(await app.SentEventsAsync(), typeof(TimeoutRejectedException));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"children":[""")]
    [InlineData(HttpStatusCode.OK, "")]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    public async Task Unreadable_or_refused_standings_are_a_warning_without_an_event(HttpStatusCode status, string body)
    {
        await using var app = new StandingsApp(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));

        Assert.Equal(0, await app.Standings.RefreshNflStandingsAsync(CancellationToken.None));

        var failure = Assert.Single(app.Logs, l => l.Category == typeof(EspnStandingsService).FullName && l.Exception is not null);
        Assert.Equal(LogLevel.Warning, failure.Level);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Own_fault_below_the_refresh_is_sent_once_at_error()
    {
        await using var app = new StandingsApp(_ => throw new InvalidOperationException("Synthetic fault below the fetch"));

        Assert.Equal(0, await app.Standings.RefreshNflStandingsAsync(CancellationToken.None));

        AssertSentOnceAtError(await app.SentEventsAsync(), typeof(InvalidOperationException));
    }

    [Fact]
    public async Task Client_deadline_on_the_refresh_is_sent_once_at_error()
    {
        await using var app = new StandingsApp(Hang, services => services.ConfigureAll<HttpClientFactoryOptions>(options =>
            options.HttpClientActions.Add(client => client.Timeout = TimeSpan.FromMilliseconds(200))));

        Assert.Equal(0, await app.Standings.RefreshNflStandingsAsync(CancellationToken.None));

        AssertSentOnceAtError(await app.SentEventsAsync(), typeof(TaskCanceledException));
    }

    [Fact]
    public async Task Database_failure_is_sent_once_at_error_with_its_sqlstate()
    {
        await using var database = IsolatedPostgreSql.CreateContainer();
        await database.StartAsync();
        var options = new DbContextOptionsBuilder<ExcitementDbContext>().UseNpgsql(database.GetConnectionString()).Options;
        await using (var db = new ExcitementDbContext(options))
            await db.Database.EnsureCreatedAsync();
        await using var holder = new NpgsqlConnection(database.GetConnectionString());
        await holder.OpenAsync();
        await using var tableLock = await holder.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("LOCK TABLE team_standings IN ACCESS EXCLUSIVE MODE", holder, tableLock))
            await lockCommand.ExecuteNonQueryAsync();
        await using var app = new StandingsApp(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Standings) }),
            services =>
            {
                services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
                services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
                services.AddDbContext<ExcitementDbContext>(db => db.UseNpgsql(database.GetConnectionString() + ";Options=-c lock_timeout=500"));
            });

        Assert.Equal(0, await app.Standings.RefreshNflStandingsAsync(CancellationToken.None));

        var sent = await app.SentEventsAsync();
        AssertSentOnceAtError(sent, typeof(PostgresException));
        var failure = Assert.Single(sent.Single().Exceptions, e => e.Type == typeof(PostgresException).FullName);
        Assert.StartsWith(PostgresErrorCodes.LockNotAvailable + ":", failure.Value);
    }

    private static void AssertSentOnceAtError(IReadOnlyCollection<SentEvent> sent, Type exception)
    {
        var reported = Assert.Single(sent);
        Assert.Equal(typeof(EspnStandingsService).FullName, reported.Logger);
        Assert.Equal("error", reported.Level);
        Assert.Equal(Template, reported.Template);
        Assert.Contains(reported.Exceptions, e => e.Type == exception.FullName);
    }

    private static async Task<HttpResponseMessage> Hang(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new UnreachableException();
    }

    private sealed record SentEvent(string? Logger, string? Level, string? Template, (string? Type, string? Value)[] Exceptions);

    private sealed record LoggedEntry(string Category, LogLevel Level, Exception? Exception);

    private sealed class StandingsApp : IAsyncDisposable
    {
        private readonly AppFactory entry = new();
        private readonly WebApplicationFactory<Program> app;
        private readonly SentTransport transport = new();
        private readonly LogRecorder logs = new();

        public StandingsApp(Func<CancellationToken, Task<HttpResponseMessage>> respond, Action<IServiceCollection>? configure = null)
        {
            Provider = new SyntheticProvider(respond);
            app = entry.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.ConfigureServices(services =>
                {
                    foreach (var worker in services.Where(s => s.ServiceType == typeof(IHostedService)
                        && s.ImplementationType?.Namespace?.StartsWith("backend", StringComparison.Ordinal) == true).ToArray())
                        services.Remove(worker);
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
        }

        public SyntheticProvider Provider { get; }

        public EspnStandingsService Standings => app.Services.GetRequiredService<EspnStandingsService>();

        public IEnumerable<LoggedEntry> Logs => logs.Entries;

        public async Task<IReadOnlyCollection<SentEvent>> SentEventsAsync()
        {
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
            return transport.Events.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            await entry.DisposeAsync();
            Provider.Dispose();
        }
    }

    private sealed class SyntheticProvider(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private readonly TaskCompletionSource firstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int requests;

        public Task FirstRequest => firstRequest.Task;

        public int Requests => requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            firstRequest.TrySetResult();
            return respond(cancellationToken);
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
                entries.Enqueue(new LoggedEntry(category, logLevel, exception));
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
                var template = root.TryGetProperty("logentry", out var logEntry) ? Text(logEntry, "message") : null;
                Events.Enqueue(new SentEvent(Text(root, "logger"), Text(root, "level"), template, exceptions));
            }
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) ? value.GetString() : null;
    }
}
