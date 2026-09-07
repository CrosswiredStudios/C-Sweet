using CSweet.Infrastructure.Setup;

namespace CSweet.AgentHost.Broker;

public sealed class ConnectorActionWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<ConnectorActionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), clock);
        do
        {
            for (var i = 0; i < 8 && !stoppingToken.IsCancellationRequested; i++)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    if (!await scope.ServiceProvider.GetRequiredService<ConnectorActionDispatchService>().ProcessNextAsync(stoppingToken)) break;
                }
                catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Connector action dispatch will inspect durable state again ({FailureType}).", error.GetType().Name);
                    break;
                }
            }
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ConnectorActionEventDispatcher>().DispatchAsync(stoppingToken);
            }
            catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Connector action notifications remain in durable state ({FailureType}).", error.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
