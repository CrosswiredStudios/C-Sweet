using CSweet.Application.WorkManagement;

namespace CSweet.AgentHost.Broker;

public sealed class WorkTrackingWorker(IServiceScopeFactory scopes, ILogger<WorkTrackingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ITrackedDelegationService>().ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Work tracking reconciliation failed."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
