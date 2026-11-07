using System.Collections.Concurrent;
using System.Text.Json;
using backend.Services.Espn;
using backend.Services.Kalshi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Xunit;

namespace backend.tests;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class ProviderFailureReportingTests
{
    private const int StandardAttempts = 4;

    [Fact]
    public async Task Provider_failure_its_caller_logs_as_a_warning_sends_no_event()
    {
        await using var app = new ReportingApp();

        using var scope = app.Services.CreateScope();
        var refreshed = await scope.ServiceProvider.GetRequiredService<KalshiPlayoffRatingsService>()
            .RefreshAsync("nhl", 2027, CancellationToken.None);

        Assert.Equal(0, refreshed);
        Assert.Equal(StandardAttempts, app.Provider.Requests);
        var warning = Assert.Single(app.Logs, l => l.Category == typeof(KalshiPlayoffRatingsService).FullName);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.IsType<HttpRequestException>(warning.Exception);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Provider_failure_its_caller_logs_as_an_error_is_sent_once_by_that_caller()
    {
        await using var app = new ReportingApp();

        using var scope = app.Services.CreateScope();
        var refreshed = await scope.ServiceProvider.GetRequiredService<EspnStandingsService>()
            .RefreshNflStandingsAsync(CancellationToken.None);

        Assert.Equal(0, refreshed);
        Assert.Equal(StandardAttempts, app.Provider.Requests);
        var reported = Assert.Single(await app.SentEventsAsync());
        Assert.Equal(typeof(EspnStandingsService).FullName, reported.Logger);
        Assert.Equal("error", reported.Level);
        Assert.Equal(typeof(HttpRequestException).FullName, Assert.Single(reported.ExceptionTypes));
    }

    private sealed record SentEvent(string? Logger, string? Level, string?[] ExceptionTypes);

    private sealed record LoggedEntry(string Category, LogLevel Level, Exception? Exception);

    private sealed class ReportingApp : IAsyncDisposable
    {
        private readonly AppFactory entry = new();
        private readonly WebApplicationFactory<Program> app;
        private readonly SentTransport transport = new();
        private readonly LogRecorder logs = new();

        public ReportingApp()
        {
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
                });
            });
        }

        public UnreachableProvider Provider { get; } = new();

        public IServiceProvider Services => app.Services;

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

    private sealed class UnreachableProvider : HttpMessageHandler
    {
        private int requests;

        public int Requests => requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            throw new HttpRequestException("synthetic provider connection failure");
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
                var exceptionTypes = root.TryGetProperty("exception", out var exception)
                    ? exception.GetProperty("values").EnumerateArray().Select(e => Text(e, "type")).ToArray()
                    : [];
                Events.Enqueue(new SentEvent(Text(root, "logger"), Text(root, "level"), exceptionTypes));
            }
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) ? value.GetString() : null;
    }
}
