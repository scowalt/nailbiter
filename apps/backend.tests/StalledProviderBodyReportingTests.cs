using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Text.Json;
using backend.Services;
using backend.Services.Espn;
using backend.Services.Highlights;
using backend.Services.Kalshi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly.Timeout;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Xunit;

namespace backend.tests;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class StalledProviderBodyReportingTests
{
    private const int StandardAttempts = 4;
    private const string Template = "Failed to refresh {Sport} standings from ESPN";
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(20);
    private static readonly DateTime LiveStart = new(2026, 11, 7, 0, 30, 0, DateTimeKind.Utc);

    private static readonly string[] DeadlineBoundClients =
    [
        YouTubeHighlightsClient.HttpClientName, MlbHighlightsIdentityClient.HttpClientName,
        nameof(NhlPowerRankingsGuruService), nameof(MoneyPuckService), nameof(NflPlayoffLeverageCollector)
    ];

    [Fact]
    public async Task Stalled_standings_body_times_out_after_retries_and_is_sent_once_at_error()
    {
        await using var app = new StalledBodyApp();

        Assert.Equal(0, await app.Standings.RefreshNflStandingsAsync(CancellationToken.None).WaitAsync(Guard));

        Assert.Equal(StandardAttempts, app.Provider.Stalled);
        var reported = Assert.Single(await app.SentEventsAsync());
        Assert.Equal(typeof(EspnStandingsService).FullName, reported.Logger);
        Assert.Equal("error", reported.Level);
        Assert.Equal(Template, reported.Template);
        Assert.Contains(reported.Exceptions, e => e.Type == typeof(TimeoutRejectedException).FullName);
    }

    [Fact]
    public async Task Caller_cancellation_during_a_stalled_body_propagates_without_an_event()
    {
        await using var app = new StalledBodyApp();
        using var caller = new CancellationTokenSource();

        var refresh = app.Standings.RefreshNflStandingsAsync(caller.Token);
        await app.Provider.FirstStall.WaitAsync(TimeSpan.FromSeconds(10));
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh.WaitAsync(Guard));
        Assert.Equal(1, app.Provider.Stalled);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Stalled_kalshi_live_markets_body_is_sent_once_at_error()
    {
        await using var app = new StalledBodyApp(uri => uri.AbsolutePath == "/trade-api/v2/markets");

        var odds = await app.Services.GetRequiredService<KalshiOddsService>()
            .GetLiveOddsAsync("nba", "BOS", "NYK", LiveStart, CancellationToken.None).WaitAsync(Guard);

        Assert.Null(odds);
        Assert.Equal(StandardAttempts, app.Provider.Stalled);
        var reported = Assert.Single(await app.SentEventsAsync());
        Assert.Equal(typeof(KalshiOddsService).FullName, reported.Logger);
        Assert.Equal("error", reported.Level);
        Assert.Equal("Failed to fetch markets from Kalshi for {Sport}", reported.Template);
        Assert.Contains(reported.Exceptions, e => e.Type == typeof(TimeoutRejectedException).FullName);
    }

    [Fact]
    public async Task Every_provider_client_without_its_own_deadline_reports_a_stalled_body_as_a_provider_failure()
    {
        await using var app = new StalledBodyApp();

        Assert.Subset(app.ClientNames.ToHashSet(), DeadlineBoundClients.ToHashSet());
        var providerClients = app.ClientNames.Except(DeadlineBoundClients).Order().ToArray();
        Assert.Contains(nameof(EspnStandingsService), providerClients);
        Assert.Contains(nameof(KalshiOddsService), providerClients);
        var clients = app.Services.GetRequiredService<IHttpClientFactory>();
        var outcomes = await Task.WhenAll(providerClients.Select(async name =>
        {
            try
            {
                await clients.CreateClient(name).GetStringAsync("https://provider.invalid/stalled").WaitAsync(Guard);
                return (name, failure: (Exception?)null);
            }
            catch (Exception failure)
            {
                return (name, failure);
            }
        }));

        var unreported = outcomes.Where(o => !ProviderFailure.Is(o.failure)).Select(o => $"{o.name}: {o.failure?.GetType().Name ?? "no failure"}").ToArray();
        Assert.True(unreported.Length == 0, string.Join(", ", unreported));
    }


    private sealed record SentEvent(string? Logger, string? Level, string? Template, (string? Type, string? Value)[] Exceptions);

    private sealed class StalledBodyApp : IAsyncDisposable
    {
        private readonly AppFactory entry = new();
        private readonly WebApplicationFactory<Program> app;
        private readonly SentTransport transport = new();

        public StalledBodyApp(Func<Uri, bool>? stalls = null)
        {
            Provider = new SyntheticProvider(stalls ?? (_ => true));
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
                    services.ConfigureAll<HttpStandardResilienceOptions>(options =>
                    {
                        options.Retry.Delay = TimeSpan.Zero;
                        options.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(50);
                    });
                    services.PostConfigure<SentryAspNetCoreOptions>(options =>
                    {
                        options.Dsn = "https://public@sentry.invalid/1";
                        options.Transport = transport;
                    });
                });
            });
        }

        public SyntheticProvider Provider { get; }

        public IReadOnlySet<string> ClientNames
        {
            get
            {
                var registryType = typeof(IHttpClientFactory).Assembly.GetType("Microsoft.Extensions.DependencyInjection.HttpClientMappingRegistry", throwOnError: true)!;
                var registry = app.Services.GetRequiredService(registryType);
                var typed = (System.Collections.IDictionary)registryType.GetProperty("NamedClientRegistrations")!.GetValue(registry)!;
                return app.Services.GetServices<IConfigureOptions<HttpClientFactoryOptions>>()
                    .OfType<ConfigureNamedOptions<HttpClientFactoryOptions>>()
                    .Select(o => o.Name)
                    .Concat(typed.Keys.Cast<string>())
                    .OfType<string>()
                    .ToHashSet();
            }
        }

        public IServiceProvider Services => app.Services;

        public EspnStandingsService Standings => app.Services.GetRequiredService<EspnStandingsService>();

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

    private sealed class SyntheticProvider(Func<Uri, bool> stalls) : HttpMessageHandler
    {
        private readonly TaskCompletionSource firstStall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int stalled;

        public Task FirstStall => firstStall.Task;

        public int Stalled => stalled;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!stalls(request.RequestUri!))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
            Interlocked.Increment(ref stalled);
            firstStall.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Pipe().Reader.AsStream()) });
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
