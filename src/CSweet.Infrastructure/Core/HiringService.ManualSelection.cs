using CSweet.Agent.SDK;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using HiringRecommendationResponse = CSweet.Contracts.Core.HiringRecommendationResponse;

namespace CSweet.Infrastructure.Core;

public sealed partial class HiringService
{
    private async Task<IReadOnlyList<HiringRecommendationResponse>> WithHiringModesAsync(IReadOnlyList<WorkforcePlan> plans,
        IReadOnlyList<HiringRecommendationResponse> cards, CancellationToken token)
    {
        var installations = plans.Select(x => x.RequestingInstallationId).Distinct().ToArray();
        var policies = await db.ChiefHiringPolicies.AsNoTracking().Where(x => installations.Contains(x.InstallationId) && x.SetupComplete)
            .ToDictionaryAsync(x => x.InstallationId, token);
        var actionKeys = plans.Select(x => $"{ChiefWorkCards.Prefix}hire:{x.Id:N}").ToArray();
        var blockedAttempts = await db.CoreWorkTasks.AsNoTracking().Where(x => !x.IsExecutable &&
            x.Status == WorkTaskStatus.Blocked && actionKeys.Contains(x.CreationIdempotencyKey) &&
            x.AssignedAgentInstallationId != null && installations.Contains(x.AssignedAgentInstallationId.Value))
            .Select(x => new { x.OrganizationId, x.AssignedAgentInstallationId, x.CreationIdempotencyKey, x.ResultSummary }).ToListAsync(token);
        var result = new List<HiringRecommendationResponse>(cards.Count);
        foreach (var card in cards)
        {
            var plan = plans.Single(x => x.Id == card.Id);
            var automatic = !plan.ManualSelectionRequestedAt.HasValue &&
                policies.TryGetValue(plan.RequestingInstallationId, out var policy) && policy.OrganizationId == plan.OrganizationId &&
                Policy(policy.SettingsJson).Mode == HiringSelectionMode.Automatic &&
                (plan.SourceResourceChangeRequestId.HasValue
                    ? (await BoundPolicyAsync(plan, token, persist: false)).Mode == HiringSelectionMode.Automatic
                    : !plan.TeamId.HasValue && !plan.WorkstreamId.HasValue);
            var blocked = automatic ? blockedAttempts.SingleOrDefault(x => x.OrganizationId == plan.OrganizationId &&
                x.AssignedAgentInstallationId == plan.RequestingInstallationId && x.CreationIdempotencyKey == $"{ChiefWorkCards.Prefix}hire:{plan.Id:N}")?.ResultSummary : null;
            result.Add(card with { AutomaticHiringEnabled = automatic, AutomaticHiringBlockReason = blocked });
        }
        return result;
    }

    public async Task<HiringRecommendationResponse?> ChooseRoleManuallyAsync(Guid organizationId, Guid recommendationId,
        Guid applicationUserId, CancellationToken token = default)
    {
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(token) : null;
        await LockHiringOrganizationAsync(organizationId, token);
        var owner = await RequireOwnerAsync(organizationId, applicationUserId, token);
        var plan = await db.WorkforcePlans.SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == recommendationId, token);
        if (plan is null) return null;
        if (db.Database.IsRelational()) await db.Entry(plan).ReloadAsync(token);
        if (plan.Status != ProposalStatus.Pending || plan.FulfilledHeadcount >= plan.Headcount)
            throw new InvalidOperationException("This role is already filled or no longer available. Refresh the hiring card.");
        if (!plan.ManualSelectionRequestedAt.HasValue)
        {
            var now = DateTimeOffset.UtcNow;
            const string reason = "The owner stopped automatic hiring and will choose this role manually.";
            plan.ManualSelectionRequestedAt = now;
            plan.ManualSelectionRequestedByOrganizationUserId = owner.Id;
            plan.SelectedCatalogAgentJson = null;
            plan.SelectionRationale = "Choose the candidate for this role.";
            plan.UpdatedAt = now;
            var workflows = await db.StaffingActionProposals.Where(x => x.OrganizationId == organizationId &&
                x.WorkforcePlanId == plan.Id && x.Status == ProposalStatus.Pending &&
                (x.DelegatedInstallationId != null || x.IdempotencyKey.StartsWith("delegated-hire:"))).ToListAsync(token);
            foreach (var workflow in workflows)
            {
                workflow.Status = ProposalStatus.Cancelled;
                workflow.DecidedByOrganizationUserId = owner.Id;
                workflow.DecidedAt = now;
                workflow.DecisionComment = reason;
            }
            var workflowIds = workflows.Select(x => x.Id).ToArray();
            var operations = await db.AgentHireOperations.Where(x => x.OrganizationId == organizationId && workflowIds.Contains(x.WorkflowId) &&
                x.Status != AgentHireOperationStatus.Succeeded && x.Status != AgentHireOperationStatus.NeedsSetup &&
                x.Status != AgentHireOperationStatus.Cancelled).ToListAsync(token);
            foreach (var operation in operations)
            {
                operation.Status = AgentHireOperationStatus.Cancelled;
                operation.Error = reason;
                operation.CompletedAt = now;
                operation.UpdatedAt = now;
                operation.LeaseOwner = null;
                operation.LeaseUntil = null;
                QueueOperationChanged(operation);
            }
            var card = await db.CoreWorkTasks.SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
                x.CreationIdempotencyKey == $"chief-work:hire:{plan.Id:N}" && !x.IsExecutable, token);
            if (card is not null && card.Status != WorkTaskStatus.Completed)
                await ChiefWorkCards.FinishActionAsync(db, card, WorkTaskStatus.Cancelled, reason, token);
            await db.SaveChangesAsync(token); // Includes hiring, operation and personal-board refresh outboxes.
            await audit.WriteAsync("hiring.recommendation.manual-selection", nameof(WorkforcePlan), plan.Id,
                $"Owner {owner.Id:D} stopped automatic hiring for {plan.Title}; {plan.FulfilledHeadcount}/{plan.Headcount} seats already filled.", cancellationToken: token);
        }
        if (transaction is not null) await transaction.CommitAsync(token);
        return ToRecommendation(plan, []);
    }
}
