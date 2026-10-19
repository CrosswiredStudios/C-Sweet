using System.Text.Json;
using CSweet.Domain.Analytics;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Analytics;

/// <summary>Accounting links from a successful claim to its authenticated delivery attempt.</summary>
public static class PersonalWorkAttribution
{
    public static async Task BindClaimAsync(CSweetDbContext db, WorkTask item, Guid installation,
        Guid attemptId, DateTimeOffset now, CancellationToken ct)
    {
        var attempt = await db.AgentWorkAttempts.Include(x => x.AgentWorkItem).SingleOrDefaultAsync(x =>
            x.Id == attemptId && x.FinishedAt == null && x.LeaseExpiresAt > now &&
            x.AgentWorkItem!.Status == AgentWorkStatus.Leased && x.AgentWorkItem.DeadlineAt > now && x.AgentWorkItem.AgentInstallationId == installation &&
            x.AgentWorkItem.OrganizationId == item.OrganizationId.ToString(), ct);
        if (attempt is null || item.AssignedAgentInstallationId != installation || item.ClaimEventId is not { } claim)
            throw new UnauthorizedAccessException("The personal claim has no active authenticated delivery attempt.");

        var prefix = $"agent-ticket-claim:{claim:N}:{item.Id:N}:";
        var receipt = db.WorkItemActivities.Local.Where(x => x.WorkItemId == item.Id &&
            x.IdempotencyKey != null && x.IdempotencyKey.StartsWith(prefix)).OrderByDescending(x => x.OccurredAt).FirstOrDefault()
            ?? await db.WorkItemActivities.Where(x => x.WorkItemId == item.Id &&
                x.IdempotencyKey != null && x.IdempotencyKey.StartsWith(prefix)).OrderByDescending(x => x.OccurredAt).FirstAsync(ct);
        // Preserve the original claim and every previous attempt binding on reconnect.
        var bindingPrefix = $"agent-ticket-binding:{receipt.Id:N}:";
        var previousBindings = await db.WorkItemActivities.AsNoTracking().Where(x =>
            x.OrganizationId == item.OrganizationId && x.WorkItemId == item.Id &&
            x.EventType == "agent.ticket.execution.bound" && x.IdempotencyKey != null &&
            x.IdempotencyKey.StartsWith(bindingPrefix)).Select(x => x.DataJson).ToListAsync(ct);
        var previousAttempts = previousBindings.Select(x => JsonSerializer.Deserialize<ClaimBinding>(x)!.agentWorkAttemptId)
            .Where(x => x != attemptId).ToArray();
        if (await db.AgentWorkAttempts.AnyAsync(x => previousAttempts.Contains(x.Id) && x.FinishedAt == null && x.LeaseExpiresAt > now &&
            x.AgentWorkItem!.Status == AgentWorkStatus.Leased && x.AgentWorkItem.DeadlineAt > now, ct))
            throw new InvalidOperationException("The personal claim is already bound to another live delivery attempt.");
        var key = bindingPrefix + attemptId.ToString("N");
        if (!db.WorkItemActivities.Local.Any(x => x.IdempotencyKey == key) &&
            !await db.WorkItemActivities.AnyAsync(x => x.OrganizationId == item.OrganizationId && x.IdempotencyKey == key, ct))
            db.WorkItemActivities.Add(new WorkItemActivity { Id = Guid.NewGuid(), OrganizationId = item.OrganizationId,
                BoardId = item.BoardId!.Value, WorkItemId = item.Id, EventType = "agent.ticket.execution.bound",
                Action = "work.personal-todo.claim.v1", ActorKind = GrantSubjectKind.AgentInstallation,
                ActorSubjectId = installation, IdempotencyKey = key,
                DataJson = JsonSerializer.Serialize(new ClaimBinding(attemptId, claim)), OccurredAt = now });
        var context = await db.WorkExecutionContexts.FindAsync([attemptId], ct);
        if (context is null)
        {
            context = new WorkExecutionContext { Id = attemptId, OrganizationId = item.OrganizationId,
                AgentInstallationId = installation, AgentWorkItemId = attempt.AgentWorkItemId };
            db.WorkExecutionContexts.Add(context);
        }
        context.RootWorkItemId = item.Id;
        context.Revision++;
        attempt.LastConfirmedAt = now;
    }

    public static async Task<bool> MatchesClaimAsync(CSweetDbContext db, WorkTask item,
        AgentWorkItem work, Guid attemptId, CancellationToken ct)
    {
        if (item.ClaimEventId is not { } claim) return false;
        if (claim.ToString("D") == work.SourceId) return true;
        var binding = JsonSerializer.Serialize(new ClaimBinding(attemptId, claim));
        bool Matches(WorkItemActivity x) => x.OrganizationId == item.OrganizationId &&
            x.WorkItemId == item.Id && x.ActorKind == GrantSubjectKind.AgentInstallation &&
            x.ActorSubjectId == work.AgentInstallationId && x.EventType == "agent.ticket.execution.bound" && x.DataJson == binding;
        return db.WorkItemActivities.Local.Any(Matches) || await db.WorkItemActivities.AsNoTracking().AnyAsync(x =>
            x.OrganizationId == item.OrganizationId && x.WorkItemId == item.Id &&
            x.ActorKind == GrantSubjectKind.AgentInstallation && x.ActorSubjectId == work.AgentInstallationId &&
            x.EventType == "agent.ticket.execution.bound" && x.DataJson == binding, ct);
    }

    public static async Task BindCoordinationAsync(CSweetDbContext db, Guid organization, Guid installation,
        Guid runtime, Guid sessionId, DateTimeOffset now, CancellationToken ct)
    {
        var attempts = await db.AgentWorkAttempts.Include(x => x.AgentWorkItem).Where(x =>
            x.RuntimeInstanceId == runtime && x.FinishedAt == null && x.LeaseExpiresAt > now &&
            x.AgentWorkItem!.OrganizationId == organization.ToString() && x.AgentWorkItem.AgentInstallationId == installation &&
            x.AgentWorkItem.Status == AgentWorkStatus.Leased && x.AgentWorkItem.DeadlineAt > now).Take(2).ToListAsync(ct);
        if (attempts.Count != 1) return;
        var attempt = attempts[0];
        var context = await db.WorkExecutionContexts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == attempt.Id &&
            x.OrganizationId == organization && x.AgentInstallationId == installation, ct);
        if (context?.RootWorkItemId is not { } rootId || context.WorkItemId is not { } focusId) return;
        var root = await db.CoreWorkTasks.Include(x => x.Board).SingleOrDefaultAsync(x => x.Id == rootId &&
            x.OrganizationId == organization && x.AssignedAgentInstallationId == installation &&
            x.Status == WorkTaskStatus.Running && x.ClaimExpiresAt > now && x.NextReviewAt == null && x.WaitingReason == null &&
            x.ArchivedAt == null && x.Board != null && x.Board.ArchivedAt == null && x.Board.Kind == WorkBoardKind.Personal, ct);
        if (root is null || !await MatchesClaimAsync(db, root, attempt.AgentWorkItem!, attempt.Id, ct)) return;
        var focus = await db.CoreWorkTasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == focusId &&
            x.OrganizationId == organization && x.AssignedAgentInstallationId == installation && x.ArchivedAt == null &&
            x.Status == WorkTaskStatus.Running && x.BoardId == root.BoardId, ct);
        if (focus is null) return;
        // This receipt carries accounting ownership only. Do not change the collaboration's
        // SourceKind/SourceWorkItemId, which also participate in execution authorization.
        db.WorkItemActivities.Add(new WorkItemActivity { Id = Guid.NewGuid(), OrganizationId = organization,
            BoardId = focus.BoardId!.Value, WorkItemId = focus.Id, EventType = "agent.ticket.coordination.bound",
            Action = "communication.coordination.start", ActorKind = GrantSubjectKind.AgentInstallation,
            ActorSubjectId = installation, IdempotencyKey = $"coordination-accounting:{sessionId:N}",
            DataJson = CoordinationBinding(sessionId), OccurredAt = now });
    }

    internal static string CoordinationBinding(Guid sessionId) => JsonSerializer.Serialize(new { coordinationSessionId = sessionId });

    private sealed record ClaimBinding(Guid agentWorkAttemptId, Guid claimEventId);
}
