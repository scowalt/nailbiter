namespace backend.Services.Highlights;

public sealed class HighlightsBackgroundService(IServiceScopeFactory scopes, TimeProvider time,
    ILogger<HighlightsBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? previousReason = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<HighlightsLifecycleService>().RunAsync(stoppingToken);
                if (result.Selected > 0 || result.Revalidated > 0 || result.Expired > 0 || result.Reason != previousReason)
                    logger.LogInformation("Highlights lifecycle: {Reason}; selected {Selected}; revalidated {Revalidated}; expired {Expired}", result.Reason, result.Selected, result.Revalidated, result.Expired);
                previousReason = result.Reason;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Highlights lifecycle skipped: {Reason}", "OperationFailure"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
