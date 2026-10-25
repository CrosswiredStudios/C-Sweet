using CSweet.Infrastructure.Setup;

namespace CSweet.AgentHost.Broker;

public sealed class PluginAccountSetupWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<PluginAccountSetupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PluginAccountService>().ProcessPendingAsync(stoppingToken);
            }
            catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            { logger.LogWarning("Account setup will recover from its durable checkpoint ({FailureType}).", error.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
