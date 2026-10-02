using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.Communications;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    // A wake is committed with its source mutation. Lease renewal, logs without a failure,
    // and monitoring itself never count as progress.
    private void CaptureProjectHealthSignals()
    {
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries().Where(x => x.State is EntityState.Added or EntityState.Modified).ToArray())
        {
            // Health signals are advisory. A malformed or unexpected source record must never veto
            // the business mutation being saved (for example, an agent adding personal work).
            try { CaptureProjectHealthSignal(entry); }
            catch (Exception exception) when (exception is InvalidOperationException or FormatException or
                JsonException or KeyNotFoundException or ArgumentException) { }
        }
    }

    private void CaptureProjectHealthSignal(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
    {
        bool Changed(params string[] names) => entry.State == EntityState.Added || names.Any(name =>
            entry.Property(name).IsModified && !Equals(entry.OriginalValues[name], entry.CurrentValues[name]));
        Guid? project = null;
        Guid id;
        var progress = false;
        string detail;
        switch (entry.Entity)
        {
            case Workstream x when Changed(nameof(x.Status), nameof(x.LifecycleStage)):
                project = x.Id; id = x.Id; detail = $"Project {x.Status}: {x.LifecycleStage}";
                progress = x.Status is not WorkstreamStatus.Blocked; break;
            case WorkTask x when !IsHealthWork(x.CorrelationId) && Changed(nameof(x.Status), nameof(x.PlanningRevision), nameof(x.AssignmentRevision), nameof(x.NextReviewAt), nameof(x.BlockReason)):
                project = HealthProjectForTask(x); id = x.Id; detail = $"{x.Identifier ?? x.Title}: {x.Status}. {x.BlockReason} {x.WaitingReason}";
                progress = x.Status is WorkTaskStatus.Completed ||
                    entry.State == EntityState.Added || (entry.State == EntityState.Modified &&
                    (Changed(nameof(x.PlanningRevision)) || Changed(nameof(x.AssignmentRevision)))); break;
            case Artifact x when Changed(nameof(x.LatestRevisionId), nameof(x.AcceptedRevisionId), nameof(x.DocumentStatus)):
                project = x.WorkstreamId; id = x.Id; detail = $"Artifact {x.Title}: {x.DocumentStatus}"; progress = true; break;
            case AgentCoordinationSession x when Changed(nameof(x.Status), nameof(x.NextTurnOrdinal)):
                project = x.WorkstreamId; id = x.Id; detail = $"Coordination {x.Subject}: {x.Status}. {x.FinalSummary}";
                progress = x.Status == AgentCoordinationStatus.Completed ||
                    (x.Status is AgentCoordinationStatus.Active or AgentCoordinationStatus.Summarizing && Changed(nameof(x.NextTurnOrdinal))); break;
            case WorkStageExecution x when Changed(nameof(x.Status), nameof(x.LastError)):
                var execution = WorkItemExecutions.Find(x.ItemExecutionId);
                project = execution is null ? null : HealthProjectForTask(CoreWorkTasks.Find(execution.WorkItemId));
                id = x.Id; detail = $"Stage {x.StageKey}: {x.Status}. {x.LastError} {x.LastSummary}";
                progress = x.Status == WorkStageExecutionStatus.Completed; break;
            case WorkSprint x when Changed(nameof(x.Status)):
                project = WorkBoards.Find(x.BoardId)?.WorkstreamId; id = x.Id;
                detail = $"Sprint {x.Name}: {x.Status}"; progress = x.Status == WorkSprintStatus.Completed; break;
            case WorkstreamDecisionRecord x when Changed(nameof(x.Status)):
                project = x.WorkstreamId; id = x.Id; detail = $"Decision: {x.Status}. {x.Summary}";
                progress = x.Status != "Pending"; break;
            case RepositoryProvisioningRequest x when Changed(nameof(x.Status)):
                project = x.WorkstreamId; id = x.Id; detail = $"Repository setup {x.Status}: {x.FailureCode}. {x.FailureMessage}";
                progress = x.Status == RepositoryProvisioningStatus.Completed; break;
            case ChatTurn x when Changed(nameof(x.Status)) && x.Status == ChatTurnStatus.Failed:
                project = CoreConversations.Find(x.ConversationId)?.WorkstreamId; id = x.Id;
                detail = $"Chat failed: {x.ErrorCode}. {x.ErrorMessage}"; break;
            case AgentWorkAttempt x when Changed(nameof(x.Error), nameof(x.FinishedAt)) && x.Error is not null:
                var work = AgentWorkItems.Find(x.AgentWorkItemId);
                if (work is null) return;
                if (IsHealthWork(work.Name))
                {
                    if (work.SourceType == "platform-event" && Guid.TryParse(work.SourceId, out var eventId) &&
                        AgentPlatformEventOutbox.Find(eventId) is { } healthEvent)
                        CaptureIncidentFailure(healthEvent, $"Diagnostic attempt {x.Id}: {x.Error}");
                    return;
                }
                project = HealthProjectForAgentWork(work); id = x.Id; detail = x.Error;
                var diagnosticId = x.Error.Split(';').FirstOrDefault(s => s.StartsWith("diagnosticId=", StringComparison.Ordinal))?[13..];
                var runtimeLog = AgentRuntimeInstances.Find(x.RuntimeInstanceId)?.LogExcerpt;
                if (Guid.TryParse(diagnosticId, out _) && runtimeLog?.Contains($"Diagnostic {diagnosticId}.", StringComparison.Ordinal) == true)
                    detail += "\n" + CorrelatedFailureBlock(runtimeLog, diagnosticId);
                break;
            case AgentPlatformEventOutboxItem x when IsHealthWork(x.EventType) && Changed(nameof(x.LastError)) && x.LastError is not null:
                CaptureIncidentFailure(x, $"Incident delivery {x.Id}: {x.LastError}");
                return;
            case AgentRuntimeInstance x when Changed(nameof(x.LogExcerpt)) && x.LogExcerpt?.Contains("Diagnostic ", StringComparison.Ordinal) == true:
                // Only retain the failure block with an exact attempt diagnostic id; never a runtime-wide tail.
                var attempts = AgentWorkAttempts.Where(a => a.RuntimeInstanceId == x.Id && a.Error != null).OrderByDescending(a => a.ClaimedAt).Take(10).ToArray();
                foreach (var attempt in attempts)
                {
                    var diagnostic = attempt.Error!.Split(';').FirstOrDefault(s => s.StartsWith("diagnosticId=", StringComparison.Ordinal))?[13..];
                    if (!Guid.TryParse(diagnostic, out _) || !x.LogExcerpt.Contains($"Diagnostic {diagnostic}.", StringComparison.Ordinal)) continue;
                    var failedWork = AgentWorkItems.Find(attempt.AgentWorkItemId);
                    if (failedWork is null || IsHealthWork(failedWork.Name)) continue;
                    var failedProject = HealthProjectForAgentWork(failedWork);
                    if (failedProject.HasValue) AddHealthSignal(failedProject.Value, nameof(AgentWorkAttempt), attempt.Id, false,
                        CorrelatedFailureBlock(x.LogExcerpt, diagnostic));
                }
                return;
            default: return;
        }
        if (project.HasValue) AddHealthSignal(project.Value, entry.Entity.GetType().Name, id, progress, detail);
    }

    internal static bool IsHealthWork(string? value) => value?.Contains("project-health", StringComparison.Ordinal) == true ||
        value?.Contains("management.incident", StringComparison.Ordinal) == true;

    private void CaptureIncidentFailure(AgentPlatformEventOutboxItem source, string error)
    {
        // Invalid or null wake data cannot authorize an incident mutation.
        if (ReadGuidProperty(source.DataJson, "incidentId") is { } id &&
            ProjectIncidents.Find(id) is { Status: "Open" } incident && incident.OrganizationId == source.OrganizationId)
            AddHealthSignal(incident.WorkstreamId, "ProjectIncidentDiagnosticFailure", incident.Id, false, error);
    }

    internal Guid? HealthProjectForTask(WorkTask? task)
    {
        if (task is null) return null;
        var boardProject = task.BoardId.HasValue ? WorkBoards.Find(task.BoardId.Value)?.WorkstreamId : null;
        if (boardProject.HasValue) return boardProject;
        // Personal work contexts serialize unset scopes as JSON null ("workstreamId": null).
        return ReadGuidProperty(task.PersonalWorkContextJson, "workstreamId");
    }

    /// <summary>
    /// Reads an optional GUID property. JsonElement.TryGetGuid throws for non-string values
    /// (including JSON null), so the value kind is checked first; malformed JSON yields null.
    /// </summary>
    internal static Guid? ReadGuidProperty(string? json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(propertyName, out var value) &&
                value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id) ? id : null;
        }
        catch (JsonException) { return null; }
    }

    private Guid? HealthProjectForAgentWork(AgentWorkItem work)
    {
        var context = WorkExecutionContexts.FirstOrDefault(x => x.AgentWorkItemId == work.Id);
        if ((context?.RootWorkItemId ?? context?.WorkItemId) is { } task) return HealthProjectForTask(CoreWorkTasks.Find(task));
        var session = AgentCoordinationSessions.FirstOrDefault(x => x.CurrentAgentWorkItemId == work.Id);
        if (session is not null) return session.WorkstreamId;
        var turn = AgentCoordinationTurns.FirstOrDefault(x => x.AgentWorkItemId == work.Id);
        if (turn is not null) return AgentCoordinationSessions.Find(turn.SessionId)?.WorkstreamId;
        if (work.SourceType == "chat-turn" && Guid.TryParse(work.SourceId, out var turnId))
        {
            var chat = ChatTurns.Find(turnId);
            return chat is null ? null : CoreConversations.Find(chat.ConversationId)?.WorkstreamId;
        }
        if (work.SourceType == "WorkStageExecution" && Guid.TryParse(work.SourceId, out var stageId))
        {
            var stage = WorkStageExecutions.Find(stageId);
            var execution = stage is null ? null : WorkItemExecutions.Find(stage.ItemExecutionId);
            return execution is null ? null : HealthProjectForTask(CoreWorkTasks.Find(execution.WorkItemId));
        }
        return null;
    }

    internal static string CorrelatedFailureBlock(string excerpt, string diagnostic)
    {
        var index = excerpt.IndexOf($"Diagnostic {diagnostic}.", StringComparison.Ordinal);
        if (index < 0) return "Correlated runtime detail is unavailable.";
        var start = excerpt.LastIndexOf("fail:", index, StringComparison.Ordinal);
        if (start < 0) start = index;
        var end = excerpt.Length;
        foreach (var marker in new[] { "\nfail:", "\ninfo:", "\nwarn:", "\nLatest runtime output:" })
        {
            var next = excerpt.IndexOf(marker, index, StringComparison.Ordinal);
            if (next >= 0) end = Math.Min(end, next);
        }
        return AuditPayloadSanitizer.RedactText(excerpt[start..Math.Min(end, start + 4096)]);
    }

    private void AddHealthSignal(Guid projectId, string kind, Guid sourceId, bool progress, string detail)
    {
        var project = Workstreams.Find(projectId);
        if (project is null) return;
        var safe = AuditPayloadSanitizer.RedactText(detail);
        safe = safe[..Math.Min(safe.Length, 8192)];
        if (ProjectHealthSignals.Local.Any(x => Entry(x).State == EntityState.Added && x.WorkstreamId == projectId &&
            x.SourceKind == kind && x.SourceId == sourceId && x.Detail == safe)) return;
        ProjectHealthSignals.Add(new() { Id = Guid.NewGuid(), OrganizationId = project.OrganizationId,
            WorkstreamId = projectId, SourceKind = kind, SourceId = sourceId, MeaningfulProgress = progress,
            OccurredAt = ExecutionClock.GetUtcNow(), Detail = safe });
    }
}
