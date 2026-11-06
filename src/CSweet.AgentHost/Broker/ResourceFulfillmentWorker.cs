using CSweet.Infrastructure.Core;

namespace CSweet.AgentHost.Broker;

/// <summary>Reconciles durable host operations, including work interrupted before its completion event.</summary>
public sealed class ResourceFulfillmentWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<ResourceFulfillmentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
        do
        {
            for (var index = 0; index < 20 && !stoppingToken.IsCancellationRequested; index++)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    if (!await scope.ServiceProvider.GetRequiredService<ResourceFulfillmentService>().ProcessNextAsync(stoppingToken)) break;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    logger.LogWarning(error, "Resource fulfillment remains durable and will reconcile after recovery.");
                    break;
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
