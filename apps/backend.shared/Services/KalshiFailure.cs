using System.Net;

namespace backend.Services;

public sealed class KalshiAccessRefusedException(HttpStatusCode status) : HttpRequestException(
    $"Kalshi answered {(int)status} ({status}). Nailbiter sends Kalshi no credentials, so no API key expired: Kalshi or its edge is refusing this server, or its market data now requires authentication.",
    null, status);

public sealed class KalshiAccessRefusalHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)) return response;
        var status = response.StatusCode;
        response.Dispose();
        throw new KalshiAccessRefusedException(status);
    }
}

public static class KalshiFailure
{
    public static bool Is(Exception? exception) => exception is KalshiAccessRefusedException || ProviderFailure.Is(exception);
}
