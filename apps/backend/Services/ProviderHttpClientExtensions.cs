namespace backend.Services;

public static class ProviderHttpClientExtensions
{
    public static IHttpClientBuilder AddProviderResilienceHandler(this IHttpClientBuilder client)
    {
        client.AddStandardResilienceHandler();
        return client.AddHttpMessageHandler(() => new ProviderResponseBufferingHandler());
    }
}

public sealed class ProviderResponseBufferingHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        try
        {
            await response.Content.LoadIntoBufferAsync(cancellationToken);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
