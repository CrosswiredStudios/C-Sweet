using CSweet.Infrastructure.Compute;

namespace CSweet.Api.Compute;

public sealed class ComputeFailedProvisionCleanupWorker(IServiceScopeFactory scopes,
    ILogger<ComputeFailedProvisionCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ComputeFailedProvisionCleanup>().RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Failed compute provisioning cleanup could not advance."); }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
