using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed partial class ExecutionWorkloadOrchestrator
{
    // The gateway supplies the authenticated node after checking its current session.
    // Reports for older attempts remain useful after cancellation, expiry or reassignment.
    public async Task<bool> ReportStoppedAsync(Guid nodeId, Guid assignmentId, long fencingEpoch,
        string providerId, string providerInstanceId, bool neverCreated,
        CancellationToken cancellationToken = default)
    {
        if (nodeId == Guid.Empty || assignmentId == Guid.Empty || fencingEpoch <= 0 ||
            string.IsNullOrWhiteSpace(providerId) || providerId.Length > 100 ||
            providerInstanceId is null || providerInstanceId.Length > 256 ||
            (neverCreated ? providerInstanceId.Length != 0 : string.IsNullOrWhiteSpace(providerInstanceId)))
            return false;

        for (var retry = 0; retry < 2; retry++)
        {
            var execution = await dbContext.ExecutionAssignmentAttempts.SingleOrDefaultAsync(
                x => x.AssignmentId == assignmentId && x.FencingEpoch == fencingEpoch, cancellationToken);
            if (execution is null || execution.ExecutionNodeId != nodeId || execution.ProviderId != providerId ||
                execution.ProviderInstanceId is not null && execution.ProviderInstanceId != providerInstanceId)
                return false;
            if (execution.StoppedAt is not null)
                return execution.NeverCreated == neverCreated && execution.ProviderInstanceId == providerInstanceId;

            var assignment = await dbContext.ExecutionWorkloadAssignments.SingleAsync(
                x => x.Id == assignmentId, cancellationToken);
            var now = timeProvider.GetUtcNow();
            execution.StoppedAt = now;
            execution.ProviderInstanceId = providerInstanceId;
            execution.NeverCreated = neverCreated;
            if (assignment.FencingEpoch == fencingEpoch && assignment.IsActive)
            {
                // A lost terminal status cannot leave a stopped attempt eligible for redispatch.
                assignment.Status = ExecutionAssignmentStatus.Failed;
                assignment.FencingEpoch++;
                assignment.LeaseExpiresAt = null;
                assignment.CompletedAt = now;
                assignment.FailureCode = "office-workload-stopped";
                assignment.SanitizedFailure = "The Office confirmed teardown before a terminal result was recorded.";
                await ReconcileToolchainBuildTerminationAsync(assignment, assignment.Status, now, cancellationToken);
            }
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                dbContext.ChangeTracker.Clear();
            }
        }
        return false;
    }
}
