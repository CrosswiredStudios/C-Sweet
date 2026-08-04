using CSweet.Infrastructure.SourceControl;

namespace CSweet.WorkerHost;

public sealed class RepositoryProvisioningWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<RepositoryProvisioningWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<RepositoryProvisioningProcessor>();
                if (await processor.TryProcessNextAsync(stoppingToken))
                    continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Managed repository provisioning iteration failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
