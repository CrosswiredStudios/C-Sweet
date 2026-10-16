using CSweet.Agent.SDK;
using CSweet.Domain.Setup;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectHealthService
{
    private async Task RetainManagerRecoveryAsync(ProjectIncident incident, Guid manager, CancellationToken token)
    {
        await WithdrawIncidentAttentionAsync(incident, token);
        incident.CurrentRecipientId = manager;
        incident.Disposition = IncidentDispositions.Investigating;
        incident.ReviewAt = clock.GetUtcNow().AddMinutes(5);
        incident.EscalateAt = clock.GetUtcNow().Add(Threshold);
        incident.Revision++;
        // The scheduled review is durable and rediscovered by ReviewDueAsync. It does not
        // claim that the interrupted task itself was retried or successfully recovered.
        db.QueueAudit(new("project-health.incident.manager-recovery", OrganizationId: incident.OrganizationId,
            EntityType: nameof(ProjectIncident), EntityId: incident.Id,
            Summary: "Routine recovery remains with the accountable manager; no owner input is required.", OccurredAt: clock.GetUtcNow()));
    }

    private sealed record ChatExecutionState(Guid? WorkId, string State, string Summary);

    private async Task<ChatExecutionState> CurrentChatExecutionAsync(ProjectIncident incident, Guid rootId, CancellationToken token)
    {
        var root = await db.ChatTurns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == rootId && x.OrganizationId == incident.OrganizationId, token);
        if (root is null) return new(null, "Unknown", "No matching execution record is available; a retry is not confirmed.");
        var turns = new List<ChatTurn> { root };
        var frontier = new[] { root.Id };
        while (frontier.Length > 0 && turns.Count < 32)
        {
            var children = await db.ChatTurns.AsNoTracking().Where(x => x.OrganizationId == incident.OrganizationId &&
                x.ConversationId == root.ConversationId && x.TargetAgentOrganizationUserId == root.TargetAgentOrganizationUserId &&
                x.RetryOfTurnId != null && frontier.Contains(x.RetryOfTurnId.Value))
                .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Take(32 - turns.Count).ToListAsync(token);
            children.RemoveAll(x => turns.Any(t => t.Id == x.Id));
            turns.AddRange(children); frontier = children.Select(x => x.Id).ToArray();
        }
        var latest = turns.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).First();
        var source = latest.Id.ToString();
        var work = await db.AgentWorkItems.AsNoTracking().Where(x => x.OrganizationId == incident.OrganizationId.ToString() &&
            x.SourceType == "chat-turn" && x.SourceId == source).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync(token);
        if (work is not null && work.DeadlineAt > clock.GetUtcNow())
        {
            if (work.Status == AgentWorkStatus.Pending)
                return new(work.Id, "Queued", "A correlated retry is queued; execution and completion are not confirmed.");
            if (work.Status == AgentWorkStatus.Leased && await db.AgentWorkAttempts.AnyAsync(x => x.AgentWorkItemId == work.Id &&
                x.Attempt == work.AttemptCount && x.FinishedAt == null && x.LeaseExpiresAt > clock.GetUtcNow(), token))
                return new(work.Id, "Running", "A correlated retry has an active execution lease; completion is not confirmed.");
        }
        return new(work?.Id, work?.Status.ToString() ?? "Unknown", "No correlated retry is currently queued or running.");
    }
}
