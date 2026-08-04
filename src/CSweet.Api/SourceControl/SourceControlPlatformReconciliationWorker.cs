using CSweet.Application.SourceControl;

namespace CSweet.Api.SourceControl;

public sealed class SourceControlPlatformReconciliationWorker(
    IServiceScopeFactory scopes,
    ILogger<SourceControlPlatformReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ISourceControlPlatformSetupService>()
                    .ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Enterprise source-control reconciliation failed.");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
