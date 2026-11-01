using System.Net;

namespace backend.Services.Espn;

public static class EspnHttpClientExtensions
{
    public static IHttpClientBuilder AddEspnHttpClient<TClient>(this IServiceCollection services)
        where TClient : class
    {
        var client = services.AddHttpClient<TClient>()
            .UseSocketsHttpHandler((handler, _) =>
                handler.AutomaticDecompression = DecompressionMethods.All);
        client.AddStandardResilienceHandler();
        return client;
    }
}
