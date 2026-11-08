using backend.Services.Highlights;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly.Timeout;
using Xunit;

namespace backend.tests;

public sealed class StalledProviderBodyTests
{
    private const int StandardAttempts = 4;
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(3);

    private static readonly string[] DeadlineBoundClients =
    [
        YouTubeHighlightsClient.HttpClientName, MlbHighlightsIdentityClient.HttpClientName,
        "NhlPowerRankingsGuruService", "MoneyPuckService", "NflPlayoffLeverageCollector"
    ];

    [Fact]
    public async Task Every_provider_client_retries_a_stalled_body_inside_its_resilience_pipeline()
    {
        using var provider = new StalledBodyProvider(_ => true);
        await using var app = new AppFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.ConfigureAll<HttpClientFactoryOptions>(options =>
            {
                options.HttpMessageHandlerBuilderActions.Add(handlers => handlers.PrimaryHandler = provider.Handler());
                options.HttpClientActions.Add(client => client.Timeout = 2 * TotalTimeout);
            });
            services.ConfigureAll<HttpStandardResilienceOptions>(options =>
            {
                options.Retry.Delay = TimeSpan.Zero;
                options.AttemptTimeout.Timeout = AttemptTimeout;
                options.TotalRequestTimeout.Timeout = TotalTimeout;
            });
        }));
        var registered = app.Services.GetServices<IConfigureOptions<HttpClientFactoryOptions>>()
            .OfType<ConfigureNamedOptions<HttpClientFactoryOptions>>()
            .Select(options => options.Name)
            .OfType<string>()
            .ToHashSet();
        Assert.Subset(registered, DeadlineBoundClients.ToHashSet());
        var names = registered.Except(DeadlineBoundClients).Order().ToArray();
        var factory = app.Services.GetRequiredService<IHttpClientFactory>();

        var outcomes = await Task.WhenAll(names.Select(async name =>
        {
            var failure = await Record.ExceptionAsync(() => factory.CreateClient(name).GetStringAsync($"https://provider.invalid/{name}"));
            return (name, failure: failure?.GetType().Name, stalled: provider.StalledRequests.Count(uri => uri.AbsolutePath == $"/{name}"));
        }));

        Assert.Contains(outcomes, outcome => outcome.name == "EspnStandingsService");
        Assert.All(outcomes, outcome => Assert.Equal((outcome.name, nameof(TimeoutRejectedException), StandardAttempts), outcome));
    }
}
