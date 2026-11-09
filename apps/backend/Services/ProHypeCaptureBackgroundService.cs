using backend.Data;

namespace backend.Services;

public sealed class ProHypeCaptureBackgroundService(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<ProHypeCaptureBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var services = scope.ServiceProvider;
                await services.GetRequiredService<IProHypeLifecycle>().CaptureAsync(stoppingToken);
                await StartHypes.CaptureCollegeAsync(services.GetRequiredService<ExcitementDbContext>(),
                    services.GetRequiredService<HypeScoreService>(), clock.GetUtcNow().UtcDateTime, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.Log(DatabaseFailure.Is(ex) || ProviderFailure.Is(ex) ? LogLevel.Error : LogLevel.Warning, ex, "Pro hype capture failed");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(30), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
