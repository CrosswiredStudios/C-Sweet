using CSweet.Infrastructure.Setup;

namespace CSweet.AgentHost.Broker;

public sealed class PluginSetupObligationWorker(IServiceScopeFactory scopes,
    TimeProvider clock, ILogger<PluginSetupObligationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PluginSetupObligationDispatcher>().DispatchAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            { logger.LogWarning(exception, "Setup obligation delivery will retry from durable state."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
