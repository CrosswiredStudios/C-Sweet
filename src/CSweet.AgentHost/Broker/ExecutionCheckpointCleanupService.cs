using CSweet.Infrastructure.Setup;

namespace CSweet.AgentHost.Broker;

internal sealed class ExecutionCheckpointCleanupService(IServiceScopeFactory scopes,ILogger<ExecutionCheckpointCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await using var scope=scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ExecutionCheckpointStore>().CollectOrphansAsync(DateTimeOffset.UtcNow,stoppingToken);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { return; }
            catch(Exception) { logger.LogWarning("Private execution checkpoint cleanup did not complete; retained state was preserved."); }
        } while(await timer.WaitForNextTickAsync(stoppingToken));
    }
}
