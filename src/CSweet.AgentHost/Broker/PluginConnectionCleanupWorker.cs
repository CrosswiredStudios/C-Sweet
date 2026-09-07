using CSweet.Infrastructure.Setup;

namespace CSweet.AgentHost.Broker;

public sealed class PluginConnectionCleanupWorker(IServiceScopeFactory scopes,
    TimeProvider clock, ILogger<PluginConnectionCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PluginConnectionCleanupService>().ProcessPendingAsync(stoppingToken);
            }
            catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                // Do not log provider exceptions or credential-bearing response content.
                logger.LogWarning("Connection cleanup will retry from durable state ({FailureType}).", error.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
