using backend.Services;
using backend.Services.Highlights;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace backend.tests;

internal static class HttpClientRegistrations
{
    public static readonly IReadOnlySet<string> DeadlineBound = new HashSet<string>
    {
        YouTubeHighlightsClient.HttpClientName, MlbHighlightsIdentityClient.HttpClientName,
        nameof(NhlPowerRankingsGuruService), nameof(MoneyPuckService), nameof(NflPlayoffLeverageCollector)
    };

    public static IReadOnlySet<string> Names(IServiceProvider services)
    {
        var registryType = typeof(IHttpClientFactory).Assembly.GetType("Microsoft.Extensions.DependencyInjection.HttpClientMappingRegistry", throwOnError: true)!;
        var registry = services.GetRequiredService(registryType);
        var typed = (System.Collections.IDictionary)registryType.GetProperty("NamedClientRegistrations")!.GetValue(registry)!;
        return services.GetServices<IConfigureOptions<HttpClientFactoryOptions>>()
            .OfType<ConfigureNamedOptions<HttpClientFactoryOptions>>()
            .Select(o => o.Name)
            .Concat(typed.Keys.Cast<string>())
            .OfType<string>()
            .ToHashSet();
    }
}
