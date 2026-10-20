using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Office.Contracts.Workloads;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Cancellation is intent. Only provider shutdown evidence completes deletion.</summary>
public sealed class InstallationRuntimeCleanup(CSweetDbContext db, IAgentWorkloadRunner workloads)
{
    public async Task<bool> TryCleanupBusinessAssignmentsAsync(Guid businessId, CancellationToken token)
    {
        var business = businessId.ToString("D");
        var businessKeys = new[] { business, businessId.ToString("N"), business.ToUpperInvariant(), businessId.ToString("N").ToUpperInvariant() };
        var active = new[] { ExecutionAssignmentStatus.Pending, ExecutionAssignmentStatus.Assigned, ExecutionAssignmentStatus.Starting,
            ExecutionAssignmentStatus.Running, ExecutionAssignmentStatus.Stopping };
        var assignments = await db.ExecutionWorkloadAssignments.AsNoTracking().Where(x => businessKeys.Contains(x.BusinessId) &&
            (active.Contains(x.Status) || x.StopEvidenceVersion != 1 ||
             db.ExecutionAssignmentAttempts.Any(a => a.AssignmentId == x.Id && a.StoppedAt == null)))
            .OrderBy(x => x.Id).Take(100).ToListAsync(token);
        var confirmed = true;
        foreach (var assignment in assignments)
        {
            var handle = new IsolationWorkloadHandle("execution-fleet", assignment.AgentRuntimeInstanceId ?? assignment.Id,
                assignment.Id.ToString("N"), WorkloadKind.Runtime);
            await workloads.DestroyAsync(handle, token);
            confirmed &= (await workloads.InspectAsync(handle, token))?.State is IsolationWorkloadState.Stopped or IsolationWorkloadState.Destroyed;
        }
        return confirmed && assignments.Count < 100;
    }

    public async Task<bool> TryCleanupAsync(Guid installationId, CancellationToken token)
    {
        var installation = await db.AgentInstallations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == installationId, token);
        if (installation?.IsEnabled == true)
            throw new InvalidOperationException("Runtime cleanup requires a disabled installation.");
        var runtimes = await db.AgentRuntimeInstances.Where(x => x.AgentInstallationId == installationId).ToListAsync(token);
        var now = DateTimeOffset.UtcNow;
        foreach (var runtime in runtimes.Where(x => AgentRuntimeInstance.IsActive(x.Status)))
        {
            runtime.TransitionTo(AgentRuntimeStatus.Stopping, now, "Installation runtime deletion requested.");
        }
        foreach (var session in await db.McpAgentSessions.Where(x => x.AgentInstallationId == installationId && x.RevokedAt == null).ToListAsync(token))
        {
            session.RevokedAt = now;
            session.RevocationReason = "Installation runtime deletion requested.";
        }
        // Serializes with runtime admission's row lock, including startup before a handle exists.
        await db.SaveChangesAsync(token);
        var ids = runtimes.Select(x => x.Id).ToArray();
        var assignments = await db.ExecutionWorkloadAssignments.AsNoTracking()
            .Where(x => x.AgentRuntimeInstanceId != null && ids.Contains(x.AgentRuntimeInstanceId.Value))
            .OrderBy(x => x.Id).ToListAsync(token);
        var confirmed = true;
        foreach (var assignment in assignments)
        {
            var handle = new IsolationWorkloadHandle("execution-fleet", assignment.AgentRuntimeInstanceId!.Value,
                assignment.Id.ToString("N"), WorkloadKind.Runtime);
            await workloads.DestroyAsync(handle, token);
            var state = await workloads.InspectAsync(handle, token);
            // A missing fleet row is not physical evidence; rows must survive until acknowledgement.
            confirmed &= state?.State is IsolationWorkloadState.Stopped or IsolationWorkloadState.Destroyed;
        }
        foreach (var runtime in runtimes)
        {
            if (runtime.IsolationProviderId is not { Length: > 0 } provider || runtime.ProviderInstanceId is not { Length: > 0 } instance)
                continue;
            if (provider == "execution-fleet")
            {
                confirmed &= assignments.Any(x => x.Id.ToString("N") == instance);
                continue;
            }
            var handle = new IsolationWorkloadHandle(provider, runtime.Id, instance, WorkloadKind.Runtime);
            await workloads.DestroyAsync(handle, token);
            var status = await workloads.InspectAsync(handle, token);
            confirmed &= status is null || status.State == IsolationWorkloadState.Destroyed;
        }
        if (!confirmed) return false;
        foreach (var runtime in runtimes)
        {
            runtime.IsolationProviderId = null;
            runtime.ProviderInstanceId = null;
            if (runtime.Status == AgentRuntimeStatus.Stopping)
                runtime.TransitionTo(AgentRuntimeStatus.Cancelled, now, "Runtime teardown confirmed.");
        }
        await db.SaveChangesAsync(token);
        return true;
    }
}

public sealed class AgentCleanupPendingException() : AgentInstallationException("Agent runtime teardown is awaiting provider confirmation; cleanup will retry automatically.");
