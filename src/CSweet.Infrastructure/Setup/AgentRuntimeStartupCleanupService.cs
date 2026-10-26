using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Office.Contracts.Workloads;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

public sealed class AgentRuntimeStartupCleanupService(
    CSweetDbContext dbContext,
    IAgentWorkloadRunner workloads,
    IOptions<AgentRuntimeManagerOptions> options,
    ILogger<AgentRuntimeStartupCleanupService> logger)
{
    public const string RestartCleanupReason = "Control-plane restart cleanup stopped the previous runtime.";
    public async Task<int> RecoverTransientStartupFailuresAsync(CancellationToken cancellationToken = default)
    {
        var installations = await dbContext.AgentInstallations.Include(x => x.Schedule)
            .Where(x => x.IsEnabled && x.Schedule != null && x.Schedule.IsEnabled &&
                x.Schedule.ActivationMode == ActivationMode.AlwaysOn && x.Schedule.ConsecutiveStartupFailures > 0 &&
                !x.RuntimeInstances.Any(r => r.DeliveryBuildId == null && (r.Status == AgentRuntimeStatus.Queued || r.Status == AgentRuntimeStatus.Starting ||
                    r.Status == AgentRuntimeStatus.WaitingForMcpSession || r.Status == AgentRuntimeStatus.Running ||
                    r.Status == AgentRuntimeStatus.CompletionReported || r.Status == AgentRuntimeStatus.Stopping)))
            .ToListAsync(cancellationToken);
        var recovered = 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var installation in installations)
        {
            if (installation.Schedule!.AutomaticStartSuppressedAt is null && installation.Schedule.NextTickAt <= now) continue;
            var runtime = await dbContext.AgentRuntimeInstances.Include(x => x.Events)
                .Where(x => x.DeliveryBuildId == null && x.AgentInstallationId == installation.Id)
                .OrderByDescending(x => x.QueuedAt).FirstOrDefaultAsync(cancellationToken);
            if (runtime is null || !AgentStartupRecovery.IsTransient(runtime)) continue;
            installation.Schedule!.AutomaticStartSuppressedAt = null;
            installation.Schedule.NextTickAt = now;
            dbContext.AgentRuntimeEvents.Add(new AgentRuntimeEvent
            {
                Id = Guid.NewGuid(), AgentRuntimeInstanceId = runtime.Id, Status = runtime.Status, OccurredAt = now,
                Reason = "Server restarted; transient startup failure is eligible for a fresh attempt."
            });
            recovered++;
        }
        // Retry state and its diagnostic record survive together. The schedule worker
        // discovers the due state; no agent must remain connected to receive a wake.
        await dbContext.SaveChangesAsync(cancellationToken);
        return recovered;
    }

    public async Task<int> CleanupAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Value.CleanupWorkloadsOnStartup)
        {
            logger.LogInformation("Agent isolated-workload cleanup on startup is disabled.");
            return 0;
        }

        var stale = await dbContext.AgentRuntimeInstances.AsNoTracking()
            .Where(instance => instance.DeliveryBuildId == null && instance.IsolationProviderId != null && instance.ProviderInstanceId != null)
            .Select(instance => new { instance.Id, instance.IsolationProviderId, instance.ProviderInstanceId })
            .ToListAsync(cancellationToken);
        var removed = 0;
        foreach (var instance in stale)
        {
            // Fleet execution survives a WorkerHost restart. A current work lease plus
            // authenticated session proves this is still owned work, not an orphan.
            var now = DateTimeOffset.UtcNow;
            if (instance.IsolationProviderId == "execution-fleet" &&
                await dbContext.AgentRuntimeInstances.AnyAsync(x => x.Id == instance.Id &&
                    x.Status == AgentRuntimeStatus.Running && x.MemoryResetRequestedAt == null &&
                    x.MemoryReadEvidenceVersion == AgentRuntimeInstance.CurrentMemoryReadEvidenceVersion &&
                    x.RuntimeDeadlineAt > now && dbContext.McpAgentSessions.Any(s => s.RuntimeInstanceId == x.Id &&
                        s.AgentInstallationId == x.AgentInstallationId && s.RevokedAt == null && s.ExpiresAt > now) &&
                    dbContext.AgentWorkAttempts.Any(a => a.RuntimeInstanceId == x.Id && a.FinishedAt == null &&
                        a.LeaseExpiresAt > now && a.AgentWorkItem!.AgentInstallationId == x.AgentInstallationId &&
                        a.AgentWorkItem.Status == AgentWorkStatus.Leased), cancellationToken))
                continue;
            var handle = new IsolationWorkloadHandle(
                instance.IsolationProviderId!, instance.Id, instance.ProviderInstanceId!, WorkloadKind.Runtime);
            try
            {
                if (await workloads.InspectAsync(handle, cancellationToken) is not null)
                {
                    dbContext.AgentRuntimeEvents.Add(new AgentRuntimeEvent { Id = Guid.NewGuid(),
                        AgentRuntimeInstanceId = instance.Id, Status = (await dbContext.AgentRuntimeInstances
                            .Where(x => x.Id == instance.Id).Select(x => x.Status).SingleAsync(cancellationToken)),
                        Reason = RestartCleanupReason, OccurredAt = now });
                    await dbContext.SaveChangesAsync(cancellationToken);
                    await workloads.DestroyAsync(handle, cancellationToken);
                }
                var status = await workloads.InspectAsync(handle, cancellationToken);
                if (status is null || status.State is IsolationWorkloadState.Stopped or IsolationWorkloadState.Destroyed)
                    removed++;
            }
            catch (AgentWorkloadException exception)
            {
                logger.LogWarning(exception,
                    "Could not clean up isolated workload {ProviderId}/{ProviderInstanceId} from a previous control-plane lifetime.",
                    handle.ProviderId, handle.ProviderInstanceId);
            }
        }
        if (removed > 0)
            logger.LogInformation("Destroyed {WorkloadCount} stale isolated workloads from a previous control-plane lifetime.", removed);
        return removed;
    }
}
