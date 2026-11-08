using System.Net;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace backend.Services;

public static class ProviderFailure
{
    public static bool Is(Exception? exception) => exception switch
    {
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: var status } =>
            status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError,
        TimeoutRejectedException or BrokenCircuitException => true,
        _ => false
    };
}
