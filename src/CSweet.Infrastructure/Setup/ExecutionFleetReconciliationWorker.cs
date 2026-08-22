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
                var candidates = await db.ExecutionNodes
                    .Where(x =>
                        (x.Status == ExecutionNodeStatus.Ready && x.LastHeartbeatAt < staleAt) ||
                        (x.Status == ExecutionNodeStatus.Offline &&
                         x.ApprovedAt != null &&
                         x.DrainingAt == null &&
                         x.RevokedAt == null &&
                         x.LastHeartbeatAt >= staleAt))
                    .ToListAsync(stoppingToken);

                var changed = 0;
                foreach (var node in candidates)
                {
                    if (ReconcileAvailability(node, now, staleAt)) changed++;
                }
                if (changed > 0) await db.SaveChangesAsync(stoppingToken);

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

    internal static bool ReconcileAvailability(
        ExecutionNode node,
        DateTimeOffset now,
        DateTimeOffset staleAt)
    {
        var nextStatus = node.Status switch
        {
            ExecutionNodeStatus.Ready when node.LastHeartbeatAt < staleAt =>
                ExecutionNodeStatus.Offline,
            ExecutionNodeStatus.Offline when
                node.ApprovedAt is not null &&
                node.DrainingAt is null &&
                node.RevokedAt is null &&
                node.LastHeartbeatAt >= staleAt =>
                ExecutionNodeStatus.Ready,
            _ => node.Status
        };

        if (nextStatus == node.Status) return false;

        node.Status = nextStatus;
        node.UpdatedAt = now;
        return true;
    }
}
