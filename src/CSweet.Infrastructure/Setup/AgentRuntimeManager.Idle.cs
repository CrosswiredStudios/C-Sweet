using AgentAttentionEvents = CSweet.Agent.SDK.AgentAttentionEvents;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed partial class AgentRuntimeManager
{
    private const string OnDemandIdleTimeoutReason = "On-demand runtime idle timeout elapsed.";
    private const string OnDemandCapacityReleaseReason = "Idle on-demand runtime released for waiting agent capacity.";

    // Called only after startup admission reports exhausted capacity. Keep ordinary warm
    // runtimes when there is room, and release at most one donor per blocked admission.
    private async Task ReleaseIdleCapacityAsync(AgentRuntimeInstance waiting, DateTimeOffset now,
        bool sameBusiness, bool samePool, CancellationToken token)
    {
        var installation = waiting.AgentInstallation!;
        if (installation.Schedule!.ActivationMode == ActivationMode.OnDemand &&
            !await dbContext.AgentWorkItems.AsNoTracking().AnyAsync(work =>
                work.AgentInstallationId == installation.Id &&
                ((work.Status == AgentWorkStatus.Pending && work.AvailableAt <= now && work.DeadlineAt > now) ||
                 work.Status == AgentWorkStatus.Leased), token))
            return;

        var candidates = dbContext.AgentRuntimeInstances.Where(runtime =>
            runtime.Id != waiting.Id && runtime.DeliveryBuildId == null &&
            (runtime.Status == AgentRuntimeStatus.Running || runtime.Status == AgentRuntimeStatus.Stopping) &&
            runtime.AgentInstallation!.Schedule!.ActivationMode == ActivationMode.OnDemand);
        if (sameBusiness)
            candidates = candidates.Where(runtime => runtime.AgentInstallation!.BusinessId == installation.BusinessId);
        if (samePool)
        {
            var pool = installation.ExecutionPoolId ?? (await SettingsAsync(token)).DefaultRuntimeExecutionPoolId;
            if (pool is null) return;
            // Use the donor's actual reservation, even if its installation/default pool changed.
            candidates = candidates.Where(runtime => runtime.ExecutionAssignments.Any(assignment =>
                assignment.ExecutionPoolId == pool &&
                (runtime.Status == AgentRuntimeStatus.Stopping || assignment.Status == ExecutionAssignmentStatus.Assigned || assignment.Status == ExecutionAssignmentStatus.Starting ||
                 assignment.Status == ExecutionAssignmentStatus.Running || assignment.Status == ExecutionAssignmentStatus.Stopping)));
        }
        // Do not drain more warm runtimes while the previous handoff still awaits teardown.
        if (await candidates.AnyAsync(runtime => runtime.Status == AgentRuntimeStatus.Stopping &&
                runtime.Reason == OnDemandCapacityReleaseReason, token)) return;

        var ids = await candidates.Where(runtime => runtime.Status == AgentRuntimeStatus.Running)
            .OrderBy(runtime => runtime.IdleDeadlineAt).ThenBy(runtime => runtime.QueuedAt)
            .Select(runtime => runtime.Id).ToListAsync(token);
        foreach (var id in ids)
        {
            var donor = await dbContext.AgentRuntimeInstances
                .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.Schedule)
                .SingleAsync(x => x.Id == id, token);
            if (await ReleaseIdleRuntimeAsync(donor, now, token, forCapacity: true)) return;
        }
    }

    /// <summary>Releases one idle OnDemand runtime through normal fenced, confirmed teardown.</summary>
    public async Task<bool> ReleaseIdleRuntimeAsync(Guid runtimeInstanceId, CancellationToken cancellationToken = default)
    {
        var instance = await dbContext.AgentRuntimeInstances
            .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.Schedule)
            .SingleOrDefaultAsync(x => x.Id == runtimeInstanceId, cancellationToken);
        if (instance?.Status != AgentRuntimeStatus.Running ||
            instance.AgentInstallation?.Schedule?.ActivationMode != ActivationMode.OnDemand)
            return false;
        return await ReleaseIdleRuntimeAsync(instance, DateTimeOffset.UtcNow, cancellationToken);
    }

    private async Task<bool> ReleaseIdleRuntimeAsync(
        AgentRuntimeInstance instance, DateTimeOffset now, CancellationToken token, bool forCapacity = false)
    {
        var reason = forCapacity ? OnDemandCapacityReleaseReason : OnDemandIdleTimeoutReason;
        // Share claim admission's durable lock: either a claim wins and keeps this runtime
        // alive, or Stopping commits before the next claim may read an input payload.
        await using (var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(token) : null)
        {
            if (dbContext.Database.IsNpgsql())
            {
                var key = $"agent-work-claim:{instance.AgentInstallation!.BusinessId}:{instance.AgentInstallationId:D}";
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", token);
                await dbContext.Entry(instance).ReloadAsync(token);
            }
            if (instance.Status != AgentRuntimeStatus.Running || instance.MemoryResetRequestedAt is not null)
                return false;
            if (!await dbContext.AgentSchedules.AsNoTracking().AnyAsync(schedule =>
                    schedule.AgentInstallationId == instance.AgentInstallationId &&
                    schedule.ActivationMode == ActivationMode.OnDemand, token))
                return false;

            // Delayed retries do not need an occupied VM. Leased work is preserved even
            // after its deadline so normal lease recovery, not idle cleanup, settles it.
            var busy = await dbContext.AgentWorkItems.AsNoTracking().AnyAsync(work =>
                work.AgentInstallationId == instance.AgentInstallationId &&
                ((work.Status == AgentWorkStatus.Pending && work.AvailableAt <= now && work.DeadlineAt > now) ||
                 work.Status == AgentWorkStatus.Leased), token);
            var lastWork = await dbContext.AgentWorkAttempts.AsNoTracking()
                .Where(attempt => attempt.RuntimeInstanceId == instance.Id && attempt.FinishedAt != null &&
                    // Routine attention bookkeeping must not keep an otherwise idle VM alive forever.
                    !(attempt.AgentWorkItem!.Kind == AgentWorkKind.Event &&
                      attempt.AgentWorkItem.Name == AgentAttentionEvents.ReviewDue))
                .MaxAsync(attempt => attempt.FinishedAt, token);
            var activityAt = instance.McpSessionEstablishedAt ?? instance.StartedAt ?? instance.QueuedAt;
            if (instance.LastInteractiveActivityAt is { } interactiveAt && interactiveAt > activityAt)
                activityAt = interactiveAt;
            if (lastWork is { } completedAt && completedAt > activityAt)
                activityAt = completedAt;
            var deadline = activityAt.AddSeconds(Math.Max(1, options.Value.InteractiveIdleTimeoutSeconds));
            if (instance.IdleDeadlineAt is null || instance.IdleDeadlineAt < deadline)
                instance.IdleDeadlineAt = deadline;
            if (busy || (!forCapacity && instance.IdleDeadlineAt > now))
            {
                await dbContext.SaveChangesAsync(token);
                if (transaction is not null) await transaction.CommitAsync(token);
                return false;
            }

            Transition(instance, AgentRuntimeStatus.Stopping, now, reason);
            await RevokeRuntimeSessionsAsync(instance.Id, now, token);
            await dbContext.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
        }
        // Provider teardown runs after admission is durably fenced. The runtime remains
        // Stopping until every remote attempt acknowledges shutdown.
        await FinishStopAsync(instance, AgentRuntimeStatus.Cancelled, reason, token);
        return true;
    }
}
