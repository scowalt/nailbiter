using System.Net;

namespace backend.Services.Espn;

public static class EspnHttpClientExtensions
{
    public static IHttpClientBuilder AddEspnHttpClient<TClient>(this IServiceCollection services)
        where TClient : class
    {
        return services.AddHttpClient<TClient>()
            .UseSocketsHttpHandler((handler, _) =>
                handler.AutomaticDecompression = DecompressionMethods.All)
            .AddProviderResilienceHandler();
    }
}
