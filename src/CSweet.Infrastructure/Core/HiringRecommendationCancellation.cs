using System.Text.Json;
using CSweet.Contracts.Realtime;
using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.Notifications;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

/// <summary>Caller owns the organization hiring lock, transaction and save. Installed employees are never removed.</summary>
internal static class HiringRecommendationCancellation
{
    internal static async Task CancelAsync(CSweetDbContext db, WorkforcePlan plan, string reason, CancellationToken token)
    {
        if (plan.FulfilledHeadcount >= plan.Headcount || plan.Status is not (ProposalStatus.Pending or ProposalStatus.Approved)) return;
        var now = DateTimeOffset.UtcNow;
        plan.Status = ProposalStatus.Cancelled; plan.DecidedAt = now; plan.UpdatedAt = now;
        var workflows = await db.StaffingActionProposals.Where(x => x.OrganizationId == plan.OrganizationId &&
            x.WorkforcePlanId == plan.Id && x.ResultOrganizationUserId == null &&
            (x.Status == ProposalStatus.Pending || x.Status == ProposalStatus.Approved)).ToListAsync(token);
        foreach (var workflow in workflows)
        {
            workflow.Status = ProposalStatus.Cancelled; workflow.DecisionComment = reason; workflow.DecidedAt = now;
        }
        var workflowIds = workflows.Select(x => x.Id).ToArray();
        var operations = await db.AgentHireOperations.Where(x => x.OrganizationId == plan.OrganizationId && workflowIds.Contains(x.WorkflowId) &&
            x.Status != AgentHireOperationStatus.Succeeded && x.Status != AgentHireOperationStatus.NeedsSetup && x.Status != AgentHireOperationStatus.Cancelled).ToListAsync(token);
        foreach (var operation in operations)
        {
            operation.Status = AgentHireOperationStatus.Cancelled; operation.Error = reason;
            operation.CompletedAt = operation.UpdatedAt = now; operation.LeaseOwner = null; operation.LeaseUntil = null;
            db.ApplicationRealtimeOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = plan.OrganizationId,
                RecipientOrganizationUserId = operation.InitiatedByOrganizationUserId, EventType = AppRealtimeEvents.AgentHireOperationChanged,
                Subject = $"organizations/{plan.OrganizationId:D}/agent-hires/{operation.Id:D}",
                DataJson = JsonSerializer.Serialize(new { operationId = operation.Id, organizationId = plan.OrganizationId }),
                Status = ApplicationRealtimeOutboxStatus.Pending, NextAttemptAt = now, OccurredAt = now });
        }
        var actions = await db.SuggestedUserActions.Where(x => x.OrganizationId == plan.OrganizationId &&
            x.Status == SuggestedUserActionStatuses.Pending).ToListAsync(token);
        foreach (var action in actions.Where(x => SuggestedUserActionParameters.ReadHiringRecommendationId(x.ParametersJson) == plan.Id))
        {
            action.Status = SuggestedUserActionStatuses.Cancelled;
            await SuggestedUserActionMaterializer.QueueStatusChangedEventAsync(db, action,
                SuggestedUserActionEvents.Cancelled, now, new { actionId = action.Id, recommendationId = plan.Id, messageId = action.ConversationMessageId, reason }, token);
        }
        await new WorkManagement.WorkItemMutationEngine(db, TimeProvider.System).SettleHiringRecommendationAsync(plan, token);
    }
}
