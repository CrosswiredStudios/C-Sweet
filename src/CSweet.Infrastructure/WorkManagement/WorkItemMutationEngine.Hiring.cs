using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Domain.Security;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkItemMutationEngine
{
    // Source settlement is platform-owned, scoped to the source's organization and installation.
    // The caller saves source changes and these effects in the same transaction.
    internal async Task SettleHiringRecommendationAsync(WorkforcePlan plan, CancellationToken token)
    {
        if (plan.Status != ProposalStatus.Cancelled &&
            !(plan.Status == ProposalStatus.Approved && plan.Headcount > 0 && plan.FulfilledHeadcount >= plan.Headcount)) return;
        var correlation = $"hiring-recommendation:{plan.Id:N}";
        var cards = await db.CoreWorkTasks.Include(x => x.Board).ThenInclude(x => x!.Columns).Where(x =>
            x.OrganizationId == plan.OrganizationId && x.AssignedAgentInstallationId == plan.RequestingInstallationId &&
            x.Board != null && x.Board.Kind == WorkBoardKind.Personal &&
            x.Board.OwnerOrganizationUserId == x.AssignedEmployeeId && x.CorrelationId == correlation && x.IsExecutable)
            .ToListAsync(token);
        foreach (var card in cards)
        {
            var status = plan.Status == ProposalStatus.Cancelled ? WorkTaskStatus.Cancelled : WorkTaskStatus.Completed;
            if (card.Status == status && card.ClaimEventId is null) continue;
            var now = clock.GetUtcNow();
            card.Status = status;
            card.BoardColumnId = ColumnForStatus(card.Board!, status).Id;
            card.ResultSummary = status == WorkTaskStatus.Cancelled
                ? "The source hiring recommendation was withdrawn." : "The platform verified all requested hiring seats were fulfilled.";
            card.BlockReason = null;
            card.ClaimEventId = null;
            card.ClaimExpiresAt = null;
            card.NextReviewAt = null;
            card.WaitingReason = null;
            card.WaitingOnOrganizationUserId = null;
            card.Revision++;
            card.UpdatedAt = now;
            AddHiringLifecycleActivity(card, plan.Id, "hiring.source-settled");
            await QueuePersonalRealtimeAsync(card, token);
            await ActivateHiringSuccessorAsync(card, token);
        }
    }

    internal async Task ReconcileHiringRecommendationsAsync(Guid organization, Guid installation, CancellationToken token)
    {
        // Join only open, exactly correlated execution cards. Completed history never consumes
        // this bounded repair page, and a pending recommendation cannot starve a terminal source.
        var plans = await (from plan in db.WorkforcePlans
            join card in db.CoreWorkTasks on "hiring-recommendation:" + plan.Id.ToString().Replace("-", "") equals card.CorrelationId
            where plan.OrganizationId == organization && plan.RequestingInstallationId == installation &&
                (plan.Status == ProposalStatus.Cancelled || plan.Status == ProposalStatus.Approved &&
                    plan.Headcount > 0 && plan.FulfilledHeadcount >= plan.Headcount) &&
                card.OrganizationId == organization && card.AssignedAgentInstallationId == installation &&
                card.Board != null && card.Board.Kind == WorkBoardKind.Personal &&
                card.Board.OwnerOrganizationUserId == card.AssignedEmployeeId &&
                card.IsExecutable && card.Status != WorkTaskStatus.Completed && card.Status != WorkTaskStatus.Cancelled
            select plan).Distinct().OrderBy(x => x.UpdatedAt).ThenBy(x => x.Id).Take(100).ToListAsync(token);
        foreach (var plan in plans) await SettleHiringRecommendationAsync(plan, token);
    }

    private async Task ActivateHiringSuccessorAsync(WorkTask settled, CancellationToken token)
    {
        if (settled.Board?.ArchivedAt is not null) return;
        var items = await db.CoreWorkTasks.Where(x => x.BoardId == settled.BoardId &&
            x.OrganizationId == settled.OrganizationId && x.AssignedAgentInstallationId == settled.AssignedAgentInstallationId &&
            x.ArchivedAt == null && x.IsExecutable && x.CorrelationId != null &&
            x.CorrelationId.StartsWith("hiring-recommendation:")).ToListAsync(token);
        var plans = await db.WorkforcePlans.Where(x => x.OrganizationId == settled.OrganizationId &&
            x.RequestingInstallationId == settled.AssignedAgentInstallationId && x.Status == ProposalStatus.Pending)
            .OrderBy(x => x.Priority).ThenBy(x => x.CreatedAt).ToListAsync(token);
        var pending = plans.Select(x => $"hiring-recommendation:{x.Id:N}").ToHashSet(StringComparer.Ordinal);
        if (items.Any(x => pending.Contains(x.CorrelationId!) &&
            x.Status is WorkTaskStatus.Ready or WorkTaskStatus.Running or WorkTaskStatus.Blocked)) return;
        var next = plans.Select(p => items.FirstOrDefault(x => x.CorrelationId == $"hiring-recommendation:{p.Id:N}" &&
            x.Status == WorkTaskStatus.Backlog)).FirstOrDefault(x => x is not null);
        if (next is null) return;
        var owner = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.Id == next.AssignedEmployeeId &&
            x.OrganizationId == next.OrganizationId && x.AgentInstallationId == next.AssignedAgentInstallationId && x.IsActive, token);
        if (owner is null) return;
        next.Status = WorkTaskStatus.Ready;
        next.BoardColumnId = ColumnForStatus(settled.Board!, WorkTaskStatus.Ready).Id;
        next.UpdatedAt = clock.GetUtcNow();
        next.Revision++;
        AddHiringLifecycleActivity(next, settled.Id, "hiring.successor-activated");
        await QueueAvailableAsync(next.OrganizationId, owner, next.BoardId!.Value, next.Id, next.UpdatedAt, token);
        await QueuePersonalRealtimeAsync(next, token);
    }

    private void AddHiringLifecycleActivity(WorkTask card, Guid source, string action) =>
        db.WorkItemActivities.Add(new()
        {
            Id = Guid.NewGuid(), OrganizationId = card.OrganizationId, BoardId = card.BoardId!.Value, WorkItemId = card.Id,
            Action = action, EventType = "work.item." + action, ActorKind = GrantSubjectKind.AutomationIdentity,
            ActorSubjectId = source, ActorDisplayName = "Hiring source lifecycle", OccurredAt = card.UpdatedAt,
            IdempotencyKey = $"hiring-source:{source:N}:{card.Id:N}:{card.Revision}",
            DataJson = JsonSerializer.Serialize(new { sourceId = source, card.CorrelationId, status = card.Status.ToString(), card.Revision }, JsonOptions)
        });
}
