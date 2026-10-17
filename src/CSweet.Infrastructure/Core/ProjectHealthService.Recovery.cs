using CSweet.Agent.SDK;
using CSweet.Domain.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Communications;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectHealthService
{
    private async Task RetainManagerRecoveryAsync(ProjectIncident incident, Guid manager, CancellationToken token)
    {
        await WithdrawIncidentAttentionAsync(incident, token);
        var technicalDiagnostic = await TechnicalPlanningDiagnosticAsync(incident, token);
        if (technicalDiagnostic is not null) RetainTechnicalPlanningDiagnosis(incident, technicalDiagnostic);
        // Reporting is not a recovery effect. Never rewind the management chain or
        // extend its deadline just because another routine status report arrived.
        if (incident.CurrentRecipientId != manager)
        {
            incident.Disposition = IncidentDispositions.Escalate;
            incident.ReviewAt = null;
            incident.Revision++;
            await NotifyAsync(incident, token);
            return;
        }
        if (technicalDiagnostic is not null)
        {
            await ForwardCoreAsync(incident, TechnicalPlanningAction, token);
            return;
        }
        incident.Disposition = IncidentDispositions.Investigating;
        var now = clock.GetUtcNow();
        incident.EscalateAt ??= now.Add(Threshold);
        incident.ReviewAt = incident.EscalateAt <= now ? null :
            new[] { now.AddMinutes(5), incident.EscalateAt.Value }.Min();
        incident.Revision++;
        // The scheduled review is durable and rediscovered by ReviewDueAsync. It does not
        // claim that the interrupted task itself was retried or successfully recovered.
        db.QueueAudit(new("project-health.incident.manager-recovery", OrganizationId: incident.OrganizationId,
            EntityType: nameof(ProjectIncident), EntityId: incident.Id,
            Summary: "Routine recovery remains with the current manager without extending its escalation deadline.", OccurredAt: now));
    }

    private async Task<ChatExecutionState> CurrentIncidentExecutionAsync(ProjectIncident incident, CancellationToken token)
    {
        if (IncidentSource(incident.Fingerprint, "chat") is { } chat)
            return await CurrentChatExecutionAsync(incident, chat, token);
        AgentWorkItem? work = null;
        var organization = incident.OrganizationId.ToString();
        if (IncidentSource(incident.Fingerprint, "coordination") is { } sessionId)
        {
            var session = await db.AgentCoordinationSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sessionId &&
                x.OrganizationId == incident.OrganizationId && x.WorkstreamId == incident.WorkstreamId, token);
            if (session?.Status is AgentCoordinationStatus.Active or AgentCoordinationStatus.Summarizing &&
                session.CurrentAgentWorkItemId is { } currentWork)
                work = await db.AgentWorkItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == currentWork &&
                    x.OrganizationId == organization && x.SourceType == "agent-coordination" && x.CorrelationId == sessionId.ToString("D") &&
                    (x.AgentInstallationId == session.InitiatorInstallationId || x.AgentInstallationId == session.TargetInstallationId), token);
        }
        else if (IncidentSource(incident.Fingerprint, "work") is { } workId)
            work = await db.AgentWorkItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workId && x.OrganizationId == organization, token);
        else
        {
            var ticketId = incident.AffectedWorkItemId ?? IncidentSource(incident.Fingerprint, "ticket") ??
                IncidentSource(incident.Fingerprint, "dependency-wait") ?? IncidentSource(incident.Fingerprint, "recovery-wait");
            var workIds = new List<Guid>();
            if (IncidentSource(incident.Fingerprint, "stage") is { } stageId)
                workIds.AddRange(await db.WorkExecutionAttempts.AsNoTracking().Where(x => x.StageExecutionId == stageId && x.AgentWorkItemId != null)
                    .OrderByDescending(x => x.CreatedAt).Select(x => x.AgentWorkItemId!.Value).Take(32).ToArrayAsync(token));
            else if (ticketId is { } ticket)
                workIds.AddRange(await db.WorkExecutionContexts.AsNoTracking().Where(x => x.OrganizationId == incident.OrganizationId &&
                    (x.WorkItemId == ticket || x.RootWorkItemId == ticket)).OrderByDescending(x => x.Revision).ThenBy(x => x.Id)
                    .Select(x => x.AgentWorkItemId).Take(32).ToArrayAsync(token));
            work = await db.AgentWorkItems.AsNoTracking().Where(x => x.OrganizationId == organization && workIds.Contains(x.Id) &&
                (x.Status == AgentWorkStatus.Pending || x.Status == AgentWorkStatus.Leased))
                .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(token);
            work ??= await db.AgentWorkItems.AsNoTracking().Where(x => x.OrganizationId == organization && workIds.Contains(x.Id))
                .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(token);
        }
        if (work?.ResourceRecoveryState == "DiagnosisRequired")
            return new(work.Id, "DiagnosisRequired", "Automatic recovery stopped after a repeated operation failure or exhausted memory policy. Retained source and attempt evidence require infrastructure diagnosis; another full coding retry is not queued.");
        if (work?.ResourceRecoveryState == "WaitingForPolicy" && work.Status == AgentWorkStatus.Pending)
            return new(work.Id, "WaitingForPolicy", $"Checkpointed work needs {work.RequestedMemoryMb} MiB and is waiting for the approved resource ceiling. No replacement is authorized yet.");
        if (work is not null && work.DeadlineAt > clock.GetUtcNow())
        {
            if (work.Status == AgentWorkStatus.Pending)
                return work.ResourceRecoveryState == "WaitingForReplacement"
                    ? new(work.Id, "WaitingForReplacement", $"The same checkpointed operation is waiting for a {work.RequestedMemoryMb} MiB VM; the previous VM must stop before it resumes.")
                    : new(work.Id, "Queued", "A correlated retry is queued; execution and completion are not confirmed.");
            if (work.Status == AgentWorkStatus.Leased && await db.AgentWorkAttempts.AnyAsync(x => x.AgentWorkItemId == work.Id &&
                x.Attempt == work.AttemptCount && x.FinishedAt == null && x.LeaseExpiresAt > clock.GetUtcNow(), token))
                return new(work.Id, "Running", "A correlated retry has an active execution lease; completion is not confirmed.");
        }
        return new(work?.Id, "Inactive", "No correlated retry is currently queued or running.");
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
