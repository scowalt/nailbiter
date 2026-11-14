using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using backend.Services;
using backend.Services.Nascar;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.RateLimiting;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Xunit;

namespace backend.tests;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class ProviderRateLimiterRejectionTests
{
    private static readonly Type[] ProviderClients =
    [
        typeof(backend.Services.Espn.EspnOddsService),
        typeof(backend.Services.Kalshi.KalshiOddsService),
        typeof(CollegeEspnClient),
        typeof(CollegeKalshiClient),
        typeof(backend.Services.Espn.EspnPowerIndexService),
        typeof(backend.Services.PlayoffStatus.PlayoffStatusService),
        typeof(backend.Services.PowerRankingsGuru.PowerRankingsGuruService),
        typeof(backend.Services.Kalshi.KalshiPlayoffRatingsService),
        typeof(ProCompetitionEvidenceService),
        typeof(backend.Services.Espn.EspnStandingsService),
        typeof(backend.Services.Backfill.NflBackfillService),
        typeof(backend.Services.OpenF1.OpenF1Service),
        typeof(backend.Services.EspnF1.EspnF1Service),
        typeof(backend.Services.Kalshi.KalshiF1OddsService),
        typeof(backend.Services.EspnNascar.EspnNascarService),
        typeof(backend.Services.Kalshi.KalshiNascarOddsService),
        typeof(NascarLiveFeedService),
        typeof(backend.Services.EspnIndyCar.EspnIndyCarService)
    ];

    private static string StandardPipeline(Type client) => $"{client.Name}-standard";

    [Fact]
    public void Provider_clients_are_exactly_the_registered_clients_with_a_resilience_handler()
    {
        using var app = new AppFactory();
        var pinned = ProviderClients.Select(client => client.Name).ToHashSet();
        var resilient = ResilientClients(app.Services);

        Assert.Empty(pinned.Except(resilient).Order().Select(name => $"missing {name}")
            .Concat(resilient.Except(pinned).Order().Select(name => $"unexpected {name}")));
    }

    private static IReadOnlySet<string> ResilientClients(IServiceProvider services)
    {
        var handlers = services.GetRequiredService<IHttpMessageHandlerFactory>();
        return HttpClientRegistrations.Names(services)
            .Where(name => Chain(handlers.CreateHandler(name)).OfType<ResilienceHandler>().Any())
            .ToHashSet();
    }

    private static IEnumerable<HttpMessageHandler> Chain(HttpMessageHandler? handler)
    {
        for (; handler is not null; handler = (handler as DelegatingHandler)?.InnerHandler)
            yield return handler;
    }

    [Fact]
    public void Each_provider_client_caps_itself_at_1000_concurrent_requests_with_no_queue()
    {
        using var app = new AppFactory();
        var options = app.Services.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>();

        var limiters = ProviderClients.Select(client => options.Get(StandardPipeline(client)).RateLimiter)
            .Select(limiter => (limiter.RateLimiter is null, limiter.DefaultRateLimiterOptions.PermitLimit, limiter.DefaultRateLimiterOptions.QueueLimit));

        Assert.All(limiters, limiter => Assert.Equal((true, 1000, 0), limiter));
    }

    [Fact]
    public async Task Local_limiter_rejection_never_reaches_the_provider_and_is_not_a_provider_failure()
    {
        await using var app = new LimitedApp();
        var http = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(NascarLiveFeedService));

        var held = http.GetStringAsync("https://provider.invalid/held");
        await app.Provider.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var rejection = await Assert.ThrowsAsync<RateLimiterRejectedException>(() => http.GetStringAsync("https://provider.invalid/rejected"));
        app.Provider.Release.SetResult();
        await held;

        Assert.Equal(["/held"], app.Provider.Requests.Select(u => u.AbsolutePath));
        Assert.False(ProviderFailure.Is(rejection));
        Assert.False(KalshiFailure.Is(rejection));
    }

    [Fact]
    public async Task Local_limiter_rejection_at_a_site_that_downgrades_provider_failures_is_sent_once_as_an_error()
    {
        await using var app = new LimitedApp();
        using var scope = app.Services.CreateScope();
        var feeds = scope.ServiceProvider.GetRequiredService<NascarLiveFeedService>();

        var held = feeds.GetLiveFeedAsync(CancellationToken.None);
        await app.Provider.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var rejected = await feeds.GetLiveFeedAsync(CancellationToken.None);
        app.Provider.Release.SetResult();
        Assert.NotNull(await held);

        Assert.Null(rejected);
        Assert.Single(app.Provider.Requests);
        var logged = Assert.Single(app.Logs, l => l.Category == typeof(NascarLiveFeedService).FullName);
        Assert.Equal(LogLevel.Error, logged.Level);
        Assert.IsType<RateLimiterRejectedException>(logged.Exception);
        var sent = Assert.Single(await app.SentEventsAsync());
        Assert.Equal(typeof(NascarLiveFeedService).FullName, sent.Logger);
        Assert.Equal("error", sent.Level);
        Assert.Contains(typeof(RateLimiterRejectedException).FullName, sent.ExceptionTypes);
    }

    private sealed record SentEvent(string? Logger, string? Level, string?[] ExceptionTypes);

    private sealed record LoggedEntry(string Category, LogLevel Level, Exception? Exception);

    private sealed class LimitedApp : IAsyncDisposable
    {
        private readonly AppFactory entry = new();
        private readonly WebApplicationFactory<Program> app;
        private readonly SentTransport transport = new();
        private readonly LogRecorder logs = new();

        public LimitedApp()
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
                    services.Configure<HttpStandardResilienceOptions>(StandardPipeline(typeof(NascarLiveFeedService)), options =>
                        options.RateLimiter.DefaultRateLimiterOptions.PermitLimit = 1);
                    services.AddSingleton<ILoggerProvider>(logs);
                    services.PostConfigure<SentryAspNetCoreOptions>(options =>
                    {
                        options.Dsn = "https://public@sentry.invalid/1";
                        options.Transport = transport;
                    });
                });
            });
        }

        public HeldProvider Provider { get; } = new();

        public IServiceProvider Services => app.Services;

        public IEnumerable<LoggedEntry> Logs => logs.Entries;

        public async Task<IReadOnlyCollection<SentEvent>> SentEventsAsync()
        {
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
            return transport.Events.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            Provider.Release.TrySetResult();
            await app.DisposeAsync();
            await entry.DisposeAsync();
            Provider.Dispose();
        }
    }

    private sealed class HeldProvider : HttpMessageHandler
    {
        public ConcurrentQueue<Uri> Requests { get; } = new();

        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!);
            Arrived.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
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
