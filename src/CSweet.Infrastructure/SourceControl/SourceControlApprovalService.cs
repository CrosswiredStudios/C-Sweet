using CSweet.Application.SourceControl;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.SourceControl;

public sealed class SourceControlApprovalService(
    CSweetDbContext db,
    TimeProvider timeProvider) : ISourceControlApprovalService
{
    public async Task<SourceControlApprovalDecisionResponse> DecideAsync(
        Guid organizationId,
        Guid applicationUserId,
        Guid approvalId,
        DecideSourceControlApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await db.CoreOrganizationUsers.SingleOrDefaultAsync(candidate =>
            candidate.OrganizationId == organizationId &&
            candidate.ApplicationUserId == applicationUserId &&
            candidate.IsActive,
            cancellationToken) ?? throw new UnauthorizedAccessException(
            "The current user is not an active member of this business.");
        if (actor.PermissionLevel < OrganizationPermissionLevel.Manager)
            throw new UnauthorizedAccessException("Only business owners and managers may decide this approval.");
        if (!request.Approved && string.IsNullOrWhiteSpace(request.Feedback))
            throw new ArgumentException("Explain why the request was rejected so the requester can act on it.");
        if (request.Feedback?.Length > 2048)
            throw new ArgumentException("Approval feedback is too long.");

        var approval = await db.SourceControlApprovals.SingleOrDefaultAsync(candidate =>
            candidate.OrganizationId == organizationId && candidate.Id == approvalId,
            cancellationToken) ?? throw new KeyNotFoundException("The source-control approval was not found.");
        var desired = request.Approved ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        if (approval.Status != ApprovalStatus.Pending)
        {
            if (approval.Status != desired || !approval.DecidedAt.HasValue)
                throw new DbUpdateConcurrencyException("This approval has already been decided differently.");
            return ToResponse(approval);
        }
        if (approval.Revision != request.ExpectedRevision)
            throw new DbUpdateConcurrencyException("This approval changed; refresh before deciding it.");

        var now = timeProvider.GetUtcNow();
        if (approval.Kind == SourceControlApprovalKind.RepositoryProvisioning)
        {
            var provisioning = await db.RepositoryProvisioningRequests.SingleAsync(candidate =>
                candidate.OrganizationId == organizationId &&
                candidate.Id == approval.ProvisioningRequestId,
                cancellationToken);
            if (provisioning.Status != RepositoryProvisioningStatus.AwaitingApproval)
                throw new DbUpdateConcurrencyException("The private-project request is no longer awaiting approval.");
            provisioning.Status = request.Approved
                ? RepositoryProvisioningStatus.Pending
                : RepositoryProvisioningStatus.Cancelled;
            provisioning.FailureCode = request.Approved ? null : "manager_rejected";
            provisioning.FailureMessage = request.Approved ? null : request.Feedback!.Trim();
            provisioning.CompletedAt = request.Approved ? null : now;
            provisioning.UpdatedAt = now;
            provisioning.Revision++;
        }
        else
        {
            var merge = await db.SourceControlMergeJobs.SingleAsync(candidate =>
                candidate.OrganizationId == organizationId &&
                candidate.Id == approval.MergeJobId,
                cancellationToken);
            if (merge.Status != SourceControlMergeStatus.AwaitingApproval)
                throw new DbUpdateConcurrencyException("The merge is no longer awaiting manager approval.");
            merge.Status = request.Approved
                ? SourceControlMergeStatus.Ready
                : SourceControlMergeStatus.Cancelled;
            merge.AdministratorApprovalId = request.Approved ? approval.Id : null;
            merge.FailureCode = request.Approved ? null : "manager_rejected";
            merge.FailureMessage = request.Approved ? null : request.Feedback!.Trim();
            merge.CompletedAt = request.Approved ? null : now;
            merge.UpdatedAt = now;
            merge.Revision++;
        }

        approval.Status = desired;
        approval.DecidedByOrganizationUserId = actor.Id;
        approval.DecisionComment = request.Feedback?.Trim();
        approval.DecidedAt = now;
        approval.UpdatedAt = now;
        approval.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(approval);
    }

    private static SourceControlApprovalDecisionResponse ToResponse(SourceControlApproval approval) => new(
        approval.Id,
        approval.Kind.ToString(),
        approval.Status.ToString(),
        approval.ProvisioningRequestId,
        approval.MergeJobId,
        approval.DecidedAt!.Value,
        approval.Revision);
}
