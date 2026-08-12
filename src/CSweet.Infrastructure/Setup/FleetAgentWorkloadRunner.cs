using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.SatelliteOffice.Contracts.Workloads;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Fleet-only implementation of the application runtime boundary.</summary>
public sealed class FleetAgentWorkloadRunner(
    CSweetDbContext dbContext,
    IExecutionWorkloadOrchestrator orchestrator) : IPluginWorkloadRunner
{
    public async Task<IsolationWorkloadHandle> CreateAndStartAsync(
        RuntimeWorkloadSpecification workload,
        AgentTrustLevel trustLevel,
        string? preferredProviderId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workload);
        if (trustLevel is not (AgentTrustLevel.UntrustedRepository or AgentTrustLevel.UntrustedMarketplace or
            AgentTrustLevel.OrganizationApproved or AgentTrustLevel.PublisherTrusted or AgentTrustLevel.BuiltIn))
            throw new AgentWorkloadException("The workload trust level is invalid.");

        var installation = await dbContext.AgentInstallations.AsNoTracking()
            .SingleAsync(x => x.Id == workload.Identity.InstallationId, cancellationToken);
        var reference = await orchestrator.SubmitAsync(new ExecutionWorkloadRequest(
            ExecutionWorkloadKind.Runtime,
            null,
            workload.WorkloadId,
            installation.ExecutionPoolId,
            workload.Identity.BusinessId,
            preferredProviderId,
            workload.GuestImage.Digest,
            workload.Artifact.Digest,
            workload.ResourceLimits.VirtualCpuCount,
            workload.ResourceLimits.MemoryMegabytes,
            workload.ResourceLimits.WritableDiskMegabytes,
            JsonSerializer.Serialize(workload)), cancellationToken);

        try
        {
            while (true)
            {
                var assignment = await ReadAsync(reference.AssignmentId, cancellationToken);
                if (assignment.Status == ExecutionAssignmentStatus.Running)
                    return Handle(workload, assignment);
                if (assignment.Status is ExecutionAssignmentStatus.Failed or ExecutionAssignmentStatus.Fenced or
                    ExecutionAssignmentStatus.Cancelled)
                    throw new AgentWorkloadException(assignment.SanitizedFailure ??
                        "The execution fleet could not start the runtime workload.");
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            await orchestrator.CancelAsync(reference.AssignmentId,
                "Runtime startup was cancelled by the control plane.", CancellationToken.None);
            throw;
        }
    }

    public async Task<IsolationWorkloadStatus?> InspectAsync(
        IsolationWorkloadHandle handle,
        CancellationToken cancellationToken = default)
    {
        if (!TryAssignmentId(handle, out var assignmentId)) return null;
        var assignment = await dbContext.ExecutionWorkloadAssignments.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == assignmentId, cancellationToken);
        if (assignment is null) return null;
        var state = assignment.Status switch
        {
            ExecutionAssignmentStatus.Pending or ExecutionAssignmentStatus.Assigned => IsolationWorkloadState.Created,
            ExecutionAssignmentStatus.Starting => IsolationWorkloadState.Starting,
            ExecutionAssignmentStatus.Running => IsolationWorkloadState.Running,
            ExecutionAssignmentStatus.Stopping => IsolationWorkloadState.Stopping,
            ExecutionAssignmentStatus.Completed or ExecutionAssignmentStatus.Cancelled => IsolationWorkloadState.Stopped,
            _ => IsolationWorkloadState.Failed
        };
        var termination = assignment.Status switch
        {
            ExecutionAssignmentStatus.Completed => IsolationTerminationReason.Completed,
            ExecutionAssignmentStatus.Cancelled => IsolationTerminationReason.Cancelled,
            ExecutionAssignmentStatus.Fenced => IsolationTerminationReason.LeaseExpired,
            ExecutionAssignmentStatus.Failed => IsolationTerminationReason.ProviderFailure,
            _ => IsolationTerminationReason.None
        };
        return new IsolationWorkloadStatus(
            handle, state, termination,
            assignment.Status == ExecutionAssignmentStatus.Completed ? 0 :
                assignment.Status is ExecutionAssignmentStatus.Failed or ExecutionAssignmentStatus.Fenced ? 1 : null,
            assignment.StartedAt, assignment.CompletedAt,
            assignment.FailureCode, assignment.SanitizedFailure);
    }

    public Task StopAsync(IsolationWorkloadHandle handle, TimeSpan gracePeriod,
        CancellationToken cancellationToken = default) => CancelAsync(handle, "Runtime stop requested.", cancellationToken);

    public Task DestroyAsync(IsolationWorkloadHandle handle,
        CancellationToken cancellationToken = default) => CancelAsync(handle, "Runtime destroy requested.", cancellationToken);

    public async Task<string> GetLogsAsync(IsolationWorkloadHandle handle, int maximumBytes,
        CancellationToken cancellationToken = default)
    {
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (!TryAssignmentId(handle, out var assignmentId)) return string.Empty;
        var result = await dbContext.ExecutionWorkloadAssignments.AsNoTracking()
            .Where(x => x.Id == assignmentId)
            .Select(x => x.ResultLogExcerpt ?? x.SanitizedFailure ?? string.Empty)
            .SingleOrDefaultAsync(cancellationToken) ?? string.Empty;
        var bytes = Encoding.UTF8.GetBytes(result);
        return Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, maximumBytes));
    }

    private async Task CancelAsync(IsolationWorkloadHandle handle, string reason, CancellationToken cancellationToken)
    {
        if (TryAssignmentId(handle, out var assignmentId))
            await orchestrator.CancelAsync(assignmentId, reason, cancellationToken);
    }

    private async Task<ExecutionWorkloadAssignment> ReadAsync(Guid id, CancellationToken cancellationToken)
    {
        var tracked = dbContext.ChangeTracker.Entries<ExecutionWorkloadAssignment>()
            .FirstOrDefault(x => x.Entity.Id == id);
        if (tracked is not null) await tracked.ReloadAsync(cancellationToken);
        return await dbContext.ExecutionWorkloadAssignments.AsNoTracking()
            .SingleAsync(x => x.Id == id, cancellationToken);
    }

    private static IsolationWorkloadHandle Handle(RuntimeWorkloadSpecification workload, ExecutionWorkloadAssignment assignment) =>
        new("execution-fleet", workload.WorkloadId, assignment.Id.ToString("N"), WorkloadKind.Runtime);

    private static bool TryAssignmentId(IsolationWorkloadHandle handle, out Guid assignmentId)
    {
        assignmentId = Guid.Empty;
        return string.Equals(handle.ProviderId, "execution-fleet", StringComparison.Ordinal) &&
            Guid.TryParseExact(handle.ProviderInstanceId, "N", out assignmentId);
    }
}
