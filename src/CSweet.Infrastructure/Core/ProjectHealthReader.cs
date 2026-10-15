using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Domain.Communications;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

internal sealed record HealthFailure(string Key, string Reason, Guid? TicketId, IReadOnlyList<ProjectDiagnosticEvidence> Evidence);
internal sealed record HealthObservation(bool Active, string? Waiting, DateTimeOffset LastProgress,
    IReadOnlyList<HealthFailure> Failures, IReadOnlySet<string> Recovered, IReadOnlyList<ProjectDiagnosticEvidence> Evidence);

public sealed partial class ProjectHealthReader(CSweetDbContext db, TimeProvider clock)
{
    internal async Task<HealthObservation> ObserveAsync(Workstream project, CancellationToken token, bool initialize = false)
    {
        var now = clock.GetUtcNow();
        var boards = await db.WorkBoards.AsNoTracking().Where(x => x.OrganizationId == project.OrganizationId &&
            x.WorkstreamId == project.Id && x.ArchivedAt == null).Select(x => x.Id).ToArrayAsync(token);
        var query = db.CoreWorkTasks.AsNoTracking().Where(x => x.OrganizationId == project.OrganizationId && x.ArchivedAt == null);
        // This string-backed column is jsonb in PostgreSQL. String.Contains becomes an invalid
        // jsonb LIKE expression; use structural containment and match only the project property.
        if (db.Database.IsNpgsql())
        {
            var context = JsonSerializer.Serialize(new { workstreamId = project.Id });
            query = query.Where(x => (x.BoardId.HasValue && boards.Contains(x.BoardId.Value)) ||
                (x.PersonalWorkContextJson != null && EF.Functions.JsonContains(x.PersonalWorkContextJson, context)));
        }
        else
            query = query.Where(x => (x.BoardId.HasValue && boards.Contains(x.BoardId.Value)) || x.PersonalWorkContextJson != null);
        var candidates = await query.ToListAsync(token);
        var tickets = candidates.Where(x => !CSweetDbContext.IsHealthWork(x.CorrelationId) && db.HealthProjectForTask(x) == project.Id).ToArray();
        var ticketIds = tickets.Select(x => x.Id).ToArray();
        var sessions = await db.AgentCoordinationSessions.AsNoTracking().Where(x => x.OrganizationId == project.OrganizationId &&
            x.WorkstreamId == project.Id).ToListAsync(token);
        var sessionIds = sessions.Select(x => x.Id).ToArray();
        var sessionKeys = sessionIds.Select(x => x.ToString("D")).ToArray();
        var coordinationWorks = await db.AgentCoordinationTurns.AsNoTracking().Where(x => sessionIds.Contains(x.SessionId) && x.AgentWorkItemId != null)
            .Select(x => x.AgentWorkItemId!.Value).ToListAsync(token);
        coordinationWorks.AddRange(sessions.Where(x => x.CurrentAgentWorkItemId.HasValue).Select(x => x.CurrentAgentWorkItemId!.Value));
        // Failed sessions clear CurrentAgentWorkItemId. Correlation survives failure, resume and restart.
        coordinationWorks.AddRange(await db.AgentWorkItems.AsNoTracking().Where(x => x.OrganizationId == project.OrganizationId.ToString() &&
            x.SourceType == "agent-coordination" && sessionKeys.Contains(x.CorrelationId!)).Select(x => x.Id).ToListAsync(token));
        var executions = await db.WorkItemExecutions.AsNoTracking().Where(x => ticketIds.Contains(x.WorkItemId)).ToListAsync(token);
        var stages = await db.WorkStageExecutions.AsNoTracking().Where(x => ticketIds.Contains(x.ItemExecution!.WorkItemId) &&
            x.StageKey == x.ItemExecution.CurrentStageKey && x.Traversal == x.ItemExecution.Traversal).ToListAsync(token);
        var stageIds = stages.Select(x => x.Id).ToArray();
        var stageWorks = await db.WorkExecutionAttempts.AsNoTracking().Where(x => stageIds.Contains(x.StageExecutionId) && x.AgentWorkItemId != null)
            .Select(x => x.AgentWorkItemId!.Value).ToListAsync(token);
        var contexts = await db.WorkExecutionContexts.AsNoTracking().Where(x => x.OrganizationId == project.OrganizationId &&
            ((x.RootWorkItemId.HasValue && ticketIds.Contains(x.RootWorkItemId.Value)) || (x.WorkItemId.HasValue && ticketIds.Contains(x.WorkItemId.Value))))
            .ToListAsync(token);
        var contextWorks = contexts.Select(x => x.AgentWorkItemId);
        var chatIds = await db.CoreConversations.AsNoTracking().Where(x => x.OrganizationId == project.OrganizationId && x.WorkstreamId == project.Id)
            .Select(x => x.Id).ToArrayAsync(token);
        var linkedTurns = await ReadLinkedChatTurnIdsAsync(project, tickets, contexts.Select(x => x.AgentWorkItemId).ToArray(), sessions, token);
        var turns = await db.ChatTurns.AsNoTracking().Where(x => x.OrganizationId == project.OrganizationId &&
            (chatIds.Contains(x.ConversationId) || linkedTurns.Contains(x.Id) ||
             x.RetryOfTurnId.HasValue && linkedTurns.Contains(x.RetryOfTurnId.Value))).ToListAsync(token);
        var successfulChatRecovery = SuccessfulChatRecovery(turns);
        var turnIds = turns.Select(x => x.Id.ToString()).ToArray();
        var chatWorks = await db.AgentWorkItems.AsNoTracking().Where(x => x.OrganizationId == project.OrganizationId.ToString() &&
            x.SourceType == "chat-turn" && turnIds.Contains(x.SourceId!)).Select(x => x.Id).ToListAsync(token);
        var workIds = coordinationWorks.Concat(stageWorks).Concat(contextWorks).Concat(chatWorks).Distinct().ToArray();
        var works = (await db.AgentWorkItems.AsNoTracking().Where(x => workIds.Contains(x.Id) && x.OrganizationId == project.OrganizationId.ToString())
            .ToListAsync(token)).Where(x => !CSweetDbContext.IsHealthWork(x.Name)).ToArray();
        workIds = works.Select(x => x.Id).ToArray();
        var attempts = await db.AgentWorkAttempts.AsNoTracking().Where(x => workIds.Contains(x.AgentWorkItemId)).ToListAsync(token);
        var failures = new List<HealthFailure>();
        var evidence = new List<ProjectDiagnosticEvidence>();
        var recovered = new HashSet<string>(StringComparer.Ordinal);
        var completedStages = await db.WorkStageExecutions.AsNoTracking().Where(x => ticketIds.Contains(x.ItemExecution!.WorkItemId) &&
            (x.Status == WorkStageExecutionStatus.Completed || x.Status == WorkStageExecutionStatus.Cancelled))
            .Select(x => x.Id).ToListAsync(token);
        foreach (var stage in completedStages) recovered.Add($"stage:{stage:N}");
        Guid? AffectedTicket(AgentWorkItem work)
        {
            var context = contexts.FirstOrDefault(x => x.AgentWorkItemId == work.Id);
            if ((context?.RootWorkItemId ?? context?.WorkItemId) is { } ticket && ticketIds.Contains(ticket)) return ticket;
            if (work.SourceType == "WorkStageExecution" && Guid.TryParse(work.SourceId, out var id))
                return executions.FirstOrDefault(x => x.Id == stages.FirstOrDefault(s => s.Id == id)?.ItemExecutionId)?.WorkItemId;
            return sessions.FirstOrDefault(x => x.CurrentAgentWorkItemId == work.Id)?.SourceWorkItemId;
        }
        CSweet.Domain.Communications.AgentCoordinationSession? Coordination(AgentWorkItem work) => work.SourceType == "agent-coordination" &&
            Guid.TryParse(work.CorrelationId, out var id) ? sessions.SingleOrDefault(x => x.Id == id) : null;
        string FailureKey(AgentWorkItem work) => Coordination(work) is { } collaboration ? $"coordination:{collaboration.Id:N}" :
            work.SourceType == "chat-turn" && Guid.TryParse(work.SourceId, out var chatTurn) ? $"chat:{chatTurn:N}" :
            work.SourceType == "WorkStageExecution" && Guid.TryParse(work.SourceId, out var stage) ? $"stage:{stage:N}" : $"work:{work.Id:N}";
        foreach (var work in works.GroupBy(FailureKey).Select(x => x.OrderByDescending(w => w.CreatedAt).First()))
        {
            var latest = attempts.Where(x => x.AgentWorkItemId == work.Id).OrderByDescending(x => x.Attempt).FirstOrDefault();
            var key = FailureKey(work);
            if (recovered.Contains(key)) continue;
            if (Coordination(work) is { Status: AgentCoordinationStatus.Active or AgentCoordinationStatus.Summarizing } current &&
                current.CurrentAgentWorkItemId != work.Id) continue;
            var affectedTicket = AffectedTicket(work);
            if (Coordination(work) is null && work.SourceType != "chat-turn" && tickets.Any(x => x.Id == affectedTicket && x.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled) ||
                Coordination(work)?.Status is AgentCoordinationStatus.Completed or AgentCoordinationStatus.Cancelled ||
                work.SourceType == "chat-turn" && turns.Any(x => x.Id.ToString() == work.SourceId &&
                    successfulChatRecovery.Contains(x.Id)))
            { recovered.Add(key); continue; }
            if (work.SourceType == "WorkStageExecution" && Guid.TryParse(work.SourceId, out var sourceStage) &&
                stages.Any(x => x.Id == sourceStage && x.Status is WorkStageExecutionStatus.Completed or WorkStageExecutionStatus.Cancelled))
            { recovered.Add(key); continue; }
            if (work.Status is AgentWorkStatus.Completed or AgentWorkStatus.Cancelled)
            {
                // A reviewer can finish its inbox task by reporting a blocked stage. Only
                // the authoritative stage outcome above can establish stage recovery.
                if (Coordination(work) is null && work.SourceType != "WorkStageExecution") recovered.Add(key);
                continue;
            }
            var error = latest?.Error ?? work.LastError;
            if (error is null && work.Status is AgentWorkStatus.Pending or AgentWorkStatus.Leased)
            {
                var runtime = await db.AgentRuntimeInstances.AsNoTracking().Where(x => x.AgentInstallationId == work.AgentInstallationId)
                    .OrderByDescending(x => x.QueuedAt).FirstOrDefaultAsync(token);
                if (runtime?.Status is AgentRuntimeStatus.Failed or AgentRuntimeStatus.StartFailed or AgentRuntimeStatus.PolicyDenied or
                    AgentRuntimeStatus.RuntimeTimedOut or AgentRuntimeStatus.McpSessionTimedOut or AgentRuntimeStatus.ExitedWithoutCompletion)
                    error = $"Agent runtime {runtime.Status}: {runtime.Reason}";
            }
            if (string.IsNullOrWhiteSpace(error) && !(work.DeadlineAt <= now || latest is { FinishedAt: null } && latest.LeaseExpiresAt <= now)) continue;
            var employee = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == project.OrganizationId && x.AgentInstallationId == work.AgentInstallationId)
                .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(token);
            var fact = new ProjectDiagnosticEvidence("AgentWorkAttempt", latest?.Id ?? work.Id, latest?.FinishedAt ?? latest?.LeaseExpiresAt ?? work.DeadlineAt,
                Safe(error ?? "Execution deadline or lease expired without completion."),
                employee.HasValue ? $"/organizations/{project.OrganizationId}/employees/{employee}" : null);
            failures.Add(new(key, Coordination(work) is null ? "Agent execution failure" : "Planning coordination stopped", affectedTicket, [fact])); evidence.Add(fact);
        }
        foreach (var stage in stages)
        {
            var key = $"stage:{stage.Id:N}";
            if (stage.Status is WorkStageExecutionStatus.Completed or WorkStageExecutionStatus.Cancelled) { recovered.Add(key); continue; }
            if (stage.Status is not (WorkStageExecutionStatus.Failed or WorkStageExecutionStatus.Blocked) || failures.Any(x => x.Key == key)) continue;
            var fact = new ProjectDiagnosticEvidence(nameof(WorkStageExecution), stage.Id, stage.UpdatedAt,
                Safe($"{stage.StageKey}: {stage.Status}. {stage.LastError} {stage.LastSummary}"));
            failures.Add(new(key, "Execution stage stopped", executions.FirstOrDefault(x => x.Id == stage.ItemExecutionId)?.WorkItemId, [fact])); evidence.Add(fact);
        }
        foreach (var session in sessions)
        {
            var key = $"coordination:{session.Id:N}";
            if (session.Status is AgentCoordinationStatus.Completed or AgentCoordinationStatus.Cancelled) { recovered.Add(key); continue; }
            if (session.Status is not (AgentCoordinationStatus.Failed or AgentCoordinationStatus.Blocked)) continue;
            var fact = new ProjectDiagnosticEvidence("AgentCoordinationSession", session.Id, session.UpdatedAt,
                Safe($"{session.Subject}: {session.Status}. {session.FinalSummary}"));
            var existing = failures.FindIndex(x => x.Key == key);
            if (existing >= 0) failures[existing] = failures[existing] with { Evidence = [.. failures[existing].Evidence, fact] };
            else failures.Add(new(key, "Planning coordination stopped", session.SourceWorkItemId, [fact]));
            evidence.Add(fact);
        }
        await ObserveDeliveryPlansAsync(project, boards, failures, recovered, evidence, token);
        foreach (var ticket in tickets)
        {
            var recoveryKey = $"recovery-wait:{ticket.Id:N}";
            var recoveryConflict = ticket.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled) &&
                (ticket.WaitingReason?.StartsWith("Delivery planning needs attention:", StringComparison.Ordinal) == true ||
                 ticket.WaitingReason?.StartsWith("Workflow staffing needs attention:", StringComparison.Ordinal) == true);
            if (!recoveryConflict) recovered.Add(recoveryKey);
            else if (!failures.Any(x => x.Key.StartsWith("delivery-plan:", StringComparison.Ordinal)))
            {
                var conflict = new ProjectDiagnosticEvidence(nameof(WorkTask), ticket.Id, ticket.UpdatedAt,
                    Safe($"{ticket.Identifier ?? ticket.Title}: {ticket.WaitingReason}"), $"/organizations/{project.OrganizationId}/work/boards/{ticket.BoardId}");
                failures.Add(new(recoveryKey, "Delivery recovery stopped on a recorded planning conflict", ticket.Id, [conflict])); evidence.Add(conflict);
            }
            var key = $"ticket:{ticket.Id:N}";
            if (ticket.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled) { recovered.Add(key); continue; }
            if (ticket.Status != WorkTaskStatus.Failed && !(ticket.Status == WorkTaskStatus.Blocked && !string.IsNullOrWhiteSpace(ticket.BlockReason) &&
                ticket.NextReviewAt == null && ticket.WaitingOnOrganizationUserId == null && string.IsNullOrWhiteSpace(ticket.WaitingReason))) continue;
            // The failed ticket is a symptom of the same execution, not a second incident.
            if (failures.Any(x => x.TicketId == ticket.Id)) continue;
            var fact = new ProjectDiagnosticEvidence(nameof(WorkTask), ticket.Id, ticket.UpdatedAt, Safe($"{ticket.Identifier}: {ticket.BlockReason ?? ticket.ResultSummary}"), $"/organizations/{project.OrganizationId}/work/boards/{ticket.BoardId}");
            failures.Add(new(key, "Ticket failed", ticket.Id, [fact])); evidence.Add(fact);
        }
        foreach (var turn in turns.Where(CSweetDbContext.IsInterruptedHealthTurn))
        {
            var key = $"chat:{turn.Id:N}";
            if (successfulChatRecovery.Contains(turn.Id)) { recovered.Add(key); continue; }
            var fact = new ProjectDiagnosticEvidence(nameof(ChatTurn), turn.Id, turn.UpdatedAt, Safe($"{turn.ErrorCode}: {turn.ErrorMessage}"), $"/organizations/{project.OrganizationId}/communications/{turn.ConversationId}");
            var existing = failures.FindIndex(x => x.Key == key);
            if (existing >= 0) failures[existing] = failures[existing] with { Evidence = [.. failures[existing].Evidence, fact] };
            else failures.Add(new(key, "Project communication failed", null, [fact]));
            evidence.Add(fact);
        }
        var provisioning = await db.RepositoryProvisioningRequests.AsNoTracking().Where(x => x.OrganizationId == project.OrganizationId && x.WorkstreamId == project.Id).ToListAsync(token);
        foreach (var request in provisioning)
        {
            var key = $"repository:{request.Id:N}";
            if (request.Status is RepositoryProvisioningStatus.Completed or RepositoryProvisioningStatus.Cancelled) { recovered.Add(key); continue; }
            if (request.Status is not (RepositoryProvisioningStatus.Failed or RepositoryProvisioningStatus.Quarantined)) continue;
            var fact = new ProjectDiagnosticEvidence(nameof(RepositoryProvisioningRequest), request.Id, request.UpdatedAt,
                Safe($"Repository setup {request.Status}: {request.FailureCode}. {request.FailureMessage}"));
            failures.Add(new(key, "Project repository setup failed", null, [fact])); evidence.Add(fact);
        }
        await ObserveDependencyWaitsAsync(project, tickets, sessions, failures, recovered, evidence, token);
        // A successful retry is authoritative recovery for a prior chat incident.
        foreach (var turn in successfulChatRecovery) recovered.Add($"chat:{turn:N}");
        var active = works.Any(w => w.Status == AgentWorkStatus.Leased && w.DeadlineAt > now && attempts.Any(a =>
            a.AgentWorkItemId == w.Id && a.Attempt == w.AttemptCount && a.FinishedAt == null && a.LeaseExpiresAt > now));
        var unfinished = tickets.Where(x => x.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled)).ToArray();
        string? waiting = null;
        if (works.Any(w => w.Status == AgentWorkStatus.Pending && w.DeadlineAt > now &&
            !failures.Any(f => f.Key == FailureKey(w))))
            waiting = "Dispatched work is queued for execution or runtime startup within its deadline.";
        if (project.LifecycleStage == "Paused") waiting = "Project intentionally paused.";
        if (unfinished.Length > 0 && unfinished.All(x => x.Status == WorkTaskStatus.WaitingForApproval ||
            x.NextReviewAt > now || (x.WaitingOnOrganizationUserId.HasValue && x.NextReviewAt == null)))
            waiting = "Recorded approval or dependency wait.";
        if (unfinished.Length > 0 && stages.Count > 0 && stages.All(x => x.Status is WorkStageExecutionStatus.WaitingForApproval or WorkStageExecutionStatus.WaitingForHuman or WorkStageExecutionStatus.Completed or WorkStageExecutionStatus.Cancelled))
            waiting = "Execution awaits recorded human review.";
        var sprints = await db.WorkSprints.AsNoTracking().Where(x => boards.Contains(x.BoardId)).ToListAsync(token);
        if (sprints.Any(x => x.Status == WorkSprintStatus.Paused) && unfinished.All(x => x.SprintId.HasValue && sprints.Any(s => s.Id == x.SprintId && s.Status == WorkSprintStatus.Paused)))
            waiting = "Sprint intentionally paused.";
        if (unfinished.Length > 0 && unfinished.All(x => x.SprintId.HasValue && sprints.Any(s => s.Id == x.SprintId && s.Status == WorkSprintStatus.Planned && s.StartsAt > now)))
            waiting = "Work scheduled for a future sprint.";
        if (unfinished.Length == 0 && await db.WorkstreamDecisions.AnyAsync(x => x.WorkstreamId == project.Id && x.Status == "Pending" && (x.DueAt == null || x.DueAt > now), token))
            waiting = "Project awaits a recorded management decision.";
        if (unfinished.Length == 0 && provisioning.Any(x => x.Status == RepositoryProvisioningStatus.AwaitingApproval))
            waiting = "Repository setup awaits recorded approval.";
        var signals = await db.ProjectHealthSignals.AsNoTracking().Where(x => x.WorkstreamId == project.Id)
            .OrderByDescending(x => x.OccurredAt).Take(500).ToListAsync(token);
        evidence.AddRange(signals.Where(x => x.SourceKind != "ProjectIncidentDiagnosticFailure")
            .Select(x => new ProjectDiagnosticEvidence(x.SourceKind, x.SourceId, x.OccurredAt, x.Detail)));
        var progressAt = (await db.ProjectHealthSignals.Where(x => x.WorkstreamId == project.Id && x.MeaningfulProgress)
            .Select(x => (DateTimeOffset?)x.OccurredAt).MaxAsync(token)) ?? project.CreatedAt;
        // Mutable UpdatedAt is a one-time rollout baseline, never a recurring progress signal.
        var historicalProgress = tickets.Where(x => initialize && x.Status == WorkTaskStatus.Completed).Select(x => x.UpdatedAt)
            .Concat(sessions.Where(x => x.Status == AgentCoordinationStatus.Completed).Select(x => x.CompletedAt ?? (initialize ? x.UpdatedAt : x.CreatedAt)))
            .Append(project.CreatedAt).Max();
        if (historicalProgress > progressAt) progressAt = historicalProgress;
        var artifactAt = await db.CoreArtifacts.Where(x => x.OrganizationId == project.OrganizationId && x.WorkstreamId == project.Id)
            .Select(x => (DateTimeOffset?)(initialize ? x.UpdatedAt : x.CreatedAt)).MaxAsync(token);
        if (artifactAt > progressAt) progressAt = artifactAt.Value;
        return new(active, waiting, progressAt, failures, recovered, evidence);
    }

    internal static string Safe(string value) => AuditPayloadSanitizer.RedactText(value)[..Math.Min(AuditPayloadSanitizer.RedactText(value).Length, 8192)];
    private static bool SuccessfulChatTurn(ChatTurn turn) => turn.Status == ChatTurnStatus.Completed ||
        turn.Status == ChatTurnStatus.CompletedWithWarnings && !CSweetDbContext.IsInterruptedHealthTurn(turn);
}
