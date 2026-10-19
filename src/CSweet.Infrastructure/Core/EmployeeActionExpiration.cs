using CSweet.Contracts.Communications;
using CSweet.Contracts.Core;
using CSweet.Contracts.Realtime;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Notifications;
using CSweet.Domain.Setup;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CSweet.Infrastructure.Core;

internal static class EmployeeActionExpiration
{
    internal const string Reason = "Expired because the requesting employee was removed from the organization.";

    internal static async Task RequireAvailableAsync(CSweetDbContext db, Guid organizationId, Guid installationId, CancellationToken token)
    {
        if (await db.AgentInstallations.AsNoTracking().AnyAsync(x => x.Id == installationId &&
                (!x.IsEnabled || !db.CoreOrganizationUsers.Any(u => u.OrganizationId == organizationId &&
                    u.AgentInstallationId == installationId && u.IsActive && u.ArchivedAt == null)), token))
            throw new UnauthorizedAccessException("The requesting employee is no longer active.");
    }

    // Caller owns the transaction, hiring lock and SaveChanges. History and completed effects survive.
    internal static async Task ExpireAsync(CSweetDbContext db, Guid organizationId,
        Guid? employeeId, Guid? installationId, DateTimeOffset now, CancellationToken token)
    {
        var plans = await db.WorkforcePlans.Where(x => x.OrganizationId == organizationId &&
            installationId.HasValue && x.RequestingInstallationId == installationId).ToListAsync(token);
        foreach (var plan in plans.Where(x => x.Status == ProposalStatus.Pending))
        {
            plan.Status = ProposalStatus.Cancelled;
            plan.DecidedAt = now;
            plan.UpdatedAt = now;
        }
        var planIds = plans.Select(x => x.Id).ToArray();
        var workflows = await db.StaffingActionProposals.Where(x => x.OrganizationId == organizationId &&
            x.Status == ProposalStatus.Pending && (planIds.Contains(x.WorkforcePlanId) ||
            (installationId.HasValue && (x.RequestingInstallationId == installationId || x.DelegatedInstallationId == installationId))))
            .ToListAsync(token);
        foreach (var workflow in workflows)
        {
            workflow.Status = ProposalStatus.Cancelled;
            workflow.DecisionComment = Reason;
            workflow.DecidedAt = now;
        }
        var workflowIds = workflows.Select(x => x.Id).ToArray();
        var operations = await db.AgentHireOperations.Where(x => x.OrganizationId == organizationId &&
            workflowIds.Contains(x.WorkflowId) && x.Status != AgentHireOperationStatus.Succeeded &&
            x.Status != AgentHireOperationStatus.NeedsSetup && x.Status != AgentHireOperationStatus.Cancelled).ToListAsync(token);
        foreach (var operation in operations)
        {
            operation.Status = AgentHireOperationStatus.Cancelled;
            operation.Error = Reason;
            operation.CompletedAt = operation.UpdatedAt = now;
            operation.LeaseOwner = null;
            operation.LeaseUntil = null;
            db.ApplicationRealtimeOutbox.Add(new ApplicationRealtimeOutboxItem
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId,
                RecipientOrganizationUserId = operation.InitiatedByOrganizationUserId,
                EventType = AppRealtimeEvents.AgentHireOperationChanged,
                Subject = $"organizations/{organizationId:D}/agent-hires/{operation.Id:D}",
                DataJson = JsonSerializer.Serialize(new { operationId = operation.Id, organizationId }),
                Status = ApplicationRealtimeOutboxStatus.Pending, NextAttemptAt = now, OccurredAt = now
            });
        }
        var actions = await db.SuggestedUserActions.Where(x => x.OrganizationId == organizationId &&
            x.Status == SuggestedUserActionStatuses.Pending).ToListAsync(token);
        foreach (var action in actions.Where(x => (installationId.HasValue && x.OriginatingInstallationId == installationId) ||
                     (SuggestedUserActionParameters.ReadHiringRecommendationId(x.ParametersJson) is { } planId && planIds.Contains(planId))))
        {
            action.Status = SuggestedUserActionStatuses.Cancelled;
            await SuggestedUserActionMaterializer.QueueStatusChangedEventAsync(db, action,
                SuggestedUserActionEvents.Cancelled, now,
                new { actionId = action.Id, messageId = action.ConversationMessageId, reason = Reason }, token);
        }
        var decisions = await db.ExecutiveDecisions.Where(x => x.OrganizationId == organizationId &&
            installationId.HasValue && x.RequestingInstallationId == installationId &&
            x.Status == ExecutiveDecisionStatus.Pending).ToListAsync(token);
        foreach (var decision in decisions)
        {
            decision.Status = ExecutiveDecisionStatus.Cancelled;
            decision.UpdatedAt = now;
        }
        var proposals = await db.ActionProposals.Where(x => x.OrganizationId == organizationId &&
            installationId.HasValue && x.AgentInstallationId == installationId && x.Status == ProposalStatus.Pending).ToListAsync(token);
        foreach (var proposal in proposals) { proposal.Status = ProposalStatus.Cancelled; proposal.DecidedAt = now; }
        var accessRequests = await db.ArtifactAccessRequests.Where(x => x.OrganizationId == organizationId &&
            x.Status == ArtifactAccessRequestStatus.Pending &&
            ((installationId.HasValue && (x.RequestingInstallationId == installationId ||
                (x.SubjectKind == GrantSubjectKind.AgentInstallation && x.SubjectId == installationId))) ||
             (employeeId.HasValue && x.SubjectKind == GrantSubjectKind.OrganizationUser && x.SubjectId == employeeId))).ToListAsync(token);
        foreach (var request in accessRequests)
        {
            request.Status = ArtifactAccessRequestStatus.Cancelled;
            request.DecidedAt = now;
            db.AgentPlatformEventOutbox.Add(new AgentPlatformEventOutboxItem
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId,
                EventType = ArtifactPlatformCapabilities.AccessDecisionEvent,
                DataJson = JsonSerializer.Serialize(new ArtifactAccessDecisionEvent(request.Id, request.ArtifactId,
                    "Cancelled", JsonSerializer.Deserialize<string[]>(request.ActionsJson) ?? [], [], [], now)),
                IdempotencyKey = $"artifact-access:{request.Id:D}:Cancelled",
                TargetInstallationId = request.RequestingInstallationId,
                Status = AgentPlatformEventOutboxStatus.Pending, NextAttemptAt = now, OccurredAt = now
            });
        }
        var resourceRequests = await db.ResourceChangeRequests.Where(x => x.OrganizationId == organizationId &&
            ((employeeId.HasValue && x.RequesterOrganizationUserId == employeeId) ||
             (installationId.HasValue && x.RequesterInstallationId == installationId)) &&
            x.Status == ResourceChangeRequestStatus.Pending).ToListAsync(token);
        foreach (var request in resourceRequests)
        {
            request.Status = ResourceChangeRequestStatus.Cancelled;
            request.DecisionComment = Reason;
            request.UpdatedAt = now;
            request.DecidedAt = now;
        }
        var replenishments = await db.StaffingReplenishmentRequests.Where(x => x.OrganizationId == organizationId &&
            ((employeeId.HasValue && x.RequesterOrganizationUserId == employeeId) ||
             (installationId.HasValue && x.RequesterInstallationId == installationId)) &&
            x.Status == StaffingReplenishmentRequestStatus.Pending).ToListAsync(token);
        foreach (var request in replenishments)
        {
            request.Status = StaffingReplenishmentRequestStatus.Cancelled;
            request.DecisionComment = Reason;
            request.UpdatedAt = now;
            request.DecidedAt = now;
        }
        var approvals = await db.SourceControlApprovals.Where(x => x.OrganizationId == organizationId &&
            ((employeeId.HasValue && x.RequestedByOrganizationUserId == employeeId) ||
             (installationId.HasValue && x.RequestedByAgentInstallationId == installationId)) &&
            x.Status == ApprovalStatus.Pending).ToListAsync(token);
        foreach (var approval in approvals)
        {
            approval.Status = ApprovalStatus.Cancelled;
            approval.DecisionComment = Reason;
            approval.DecidedAt = approval.UpdatedAt = now;
            approval.Revision++;
        }
        var provisioningIds = approvals.Where(x => x.ProvisioningRequestId.HasValue).Select(x => x.ProvisioningRequestId!.Value).ToArray();
        var provisioningRequests = await db.RepositoryProvisioningRequests.Where(x => x.OrganizationId == organizationId &&
            provisioningIds.Contains(x.Id) && x.Status == RepositoryProvisioningStatus.AwaitingApproval).ToListAsync(token);
        foreach (var request in provisioningRequests)
        {
            request.Status = RepositoryProvisioningStatus.Cancelled;
            request.FailureCode = "employee_removed";
            request.FailureMessage = Reason;
            request.CompletedAt = request.UpdatedAt = now;
            request.Revision++;
        }
        var mergeIds = approvals.Where(x => x.MergeJobId.HasValue).Select(x => x.MergeJobId!.Value).ToArray();
        var merges = await db.SourceControlMergeJobs.Where(x => x.OrganizationId == organizationId &&
            mergeIds.Contains(x.Id) && x.Status == SourceControlMergeStatus.AwaitingApproval).ToListAsync(token);
        foreach (var merge in merges)
        {
            merge.Status = SourceControlMergeStatus.Cancelled;
            merge.FailureCode = "employee_removed";
            merge.FailureMessage = Reason;
            merge.CompletedAt = merge.UpdatedAt = now;
            merge.Revision++;
        }
        var executions = await db.ConnectorExecutions.Where(x => x.OrganizationId == organizationId &&
            installationId.HasValue && x.RequesterInstallationId == installationId &&
            (x.Status == "Prepared" || x.Status == "AwaitingApproval" || x.Status == "Approved")).ToListAsync(token);
        foreach (var execution in executions)
        {
            execution.Status = "Cancelled";
            execution.UpdatedAt = now;
            execution.ExpiresAt = now;
            execution.Revision++;
        }
        var auditCards = await db.CoreWorkTasks.Where(x => x.OrganizationId == organizationId &&
            !x.IsExecutable && x.CreationIdempotencyKey != null && x.CreationIdempotencyKey.StartsWith(ChiefWorkCards.Prefix) &&
            ((employeeId.HasValue && x.CreatedByOrganizationUserId == employeeId) ||
             (installationId.HasValue && x.AssignedAgentInstallationId == installationId)) &&
            x.Status != WorkTaskStatus.Completed && x.Status != WorkTaskStatus.Cancelled && x.Status != WorkTaskStatus.Failed).ToListAsync(token);
        foreach (var card in auditCards)
            await new WorkItemMutationEngine(db, TimeProvider.System).SetChiefAuditCardStateAsync(card, WorkTaskStatus.Cancelled, Reason, now, token);
    }

    // Repairs leftovers from dismissals made before expiration was implemented. A disabled,
    // detached installation cannot regain authority by leaving an old recommendation behind.
    internal static async Task ReconcileAsync(CSweetDbContext db, Guid organizationId, CancellationToken token)
    {
        var businessId = organizationId.ToString("D");
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(token) : null;
        await HiringOrganizationLock.AcquireAsync(db, organizationId, token);
        var inactive = await db.AgentInstallations.AsNoTracking().Where(x => x.BusinessId == businessId &&
            !x.IsEnabled && !db.CoreOrganizationUsers.Any(u => u.OrganizationId == organizationId &&
                u.AgentInstallationId == x.Id && u.IsActive && u.ArchivedAt == null))
            .Select(x => x.Id).ToListAsync(token);
        if (inactive.Count == 0) return;
        foreach (var installationId in inactive)
            await ExpireAsync(db, organizationId, null, installationId, DateTimeOffset.UtcNow, token);
        await db.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
    }
}
