using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Setup;

public sealed class ExecutionFleetReconciliationWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ExecutionFleetReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
                var now = timeProvider.GetUtcNow();
                var staleAt = now.AddSeconds(-30);
                var stale = await db.ExecutionNodes
                    .Where(x => x.Status == ExecutionNodeStatus.Ready && x.LastHeartbeatAt < staleAt)
                    .ToListAsync(stoppingToken);
                foreach (var node in stale)
                {
                    node.Status = ExecutionNodeStatus.Offline;
                    node.UpdatedAt = now;
                }
                if (stale.Count > 0) await db.SaveChangesAsync(stoppingToken);

                var orchestrator = scope.ServiceProvider.GetRequiredService<IExecutionWorkloadOrchestrator>();
                await orchestrator.FenceExpiredAsync(stoppingToken);
                await orchestrator.AssignPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Execution-fleet reconciliation failed.");
            }
        }
    }
}
