using CSweet.Application.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Setup;

public sealed class AgentScheduleWorker(IServiceScopeFactory scopeFactory, ILogger<AgentScheduleWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var manager = scope.ServiceProvider.GetRequiredService<IAgentRuntimeManager>();
                await manager.EnsureAlwaysOnRuntimesAsync(stoppingToken);
                await manager.EnsurePendingOnDemandRuntimesAsync(stoppingToken);
                await manager.ProcessDueSchedulesAsync(stoppingToken);
                await manager.ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "The agent schedule worker iteration failed."); }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }
}
