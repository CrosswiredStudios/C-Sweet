using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Analytics;

/// <summary>Accounting ownership only. These links never authorize execution or a source read.</summary>
public static class InferenceSourceAttribution
{
    public static async Task CaptureAsync(CSweetDbContext db, AgentRunLog log, Guid? workId,
        bool historical, CancellationToken ct = default)
    {
        if (log.MemoryErasedAt is not null || log.OrganizationId is not { } org || log.WorkItemId.HasValue || log.WorkstreamId.HasValue) return;
        var at = log.ProviderStartedAt ?? log.StartedAt;
        var work = workId.HasValue ? await db.AgentWorkItems.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == workId && x.OrganizationId == org.ToString() && x.AgentInstallationId == log.AgentInstallationId, ct) : null;
        Guid? itemId = null, projectId = null;
        var kind = "Unknown";

        if (historical && log.AgentWorkAttemptId is { } attemptId && work is not null &&
            await db.AgentWorkAttempts.AnyAsync(x => x.Id == attemptId && x.AgentWorkItemId == work.Id, ct))
        {
            var intervals = await db.WorkExecutionIntervals.AsNoTracking().Where(x => x.OrganizationId == org &&
                x.AgentWorkAttemptId == attemptId && x.StartedAt <= at && x.ConfirmedThrough >= at &&
                (x.EndedAt == null || x.EndedAt > at)).Take(2).ToListAsync(ct);
            if (intervals.Count == 1 && (intervals[0].WorkItemId.HasValue || intervals[0].WorkstreamId.HasValue))
            {
                log.WorkItemId = intervals[0].WorkItemId;
                log.WorkstreamId = intervals[0].WorkstreamId;
                log.AncestorWorkItemIdsJson = intervals[0].AncestorWorkItemIdsJson;
                log.AttributionKind = "ExecutionBackfill";
                return;
            }
        }
        if (work?.SourceType == "agent-coordination")
        {
            // Historical turns keep the exact delivery link after CurrentAgentWorkItemId advances.
            // CorrelationId is server-owned and also covers retained/retried deliveries.
            var sessions = await db.AgentCoordinationSessions.AsNoTracking().Where(x => x.OrganizationId == org &&
                (x.CurrentAgentWorkItemId == work.Id || x.Turns.Any(t => t.AgentWorkItemId == work.Id) ||
                    x.Id.ToString() == work.CorrelationId) &&
                (x.InitiatorInstallationId == work.AgentInstallationId || x.TargetInstallationId == work.AgentInstallationId) &&
                x.CreatedAt <= at).Take(2).ToListAsync(ct);
            if (sessions.Count == 1)
            {
                itemId = sessions[0].SourceWorkItemId;
                projectId = sessions[0].WorkstreamId;
                kind = "Coordination";
            }
        }
        else if (work?.SourceType == "chat-turn" && Guid.TryParse(work.SourceId, out var turnId))
        {
            // Ignore caller-supplied telemetry IDs; use the authenticated inbox source.
            var conversation = await db.ChatTurns.AsNoTracking().Where(x => x.Id == turnId && x.OrganizationId == org &&
                db.CoreOrganizationUsers.Any(u => u.Id == x.TargetAgentOrganizationUserId && u.OrganizationId == org &&
                    u.AgentInstallationId == work.AgentInstallationId))
                .Select(x => x.Conversation).SingleOrDefaultAsync(ct);
            if (conversation?.OrganizationId == org && (!historical || conversation.UpdatedAt <= at))
            { projectId = conversation.WorkstreamId; kind = "Conversation"; }
        }
        if (itemId is null && work?.SourceType == "WorkStageExecution" && Guid.TryParse(work.SourceId, out var stageId))
        {
            itemId = await db.WorkStageExecutions.Where(x => x.Id == stageId &&
                x.AgentInstallationId == work.AgentInstallationId && x.Attempts.Any(a => a.AgentWorkItemId == work.Id) &&
                x.ItemExecution!.SprintExecution!.OrganizationId == org)
                .Select(x => (Guid?)x.ItemExecution!.WorkItemId).SingleOrDefaultAsync(ct);
            kind = "Stage";
        }
        if (itemId is null && work?.SourceType == "platform-event" && Guid.TryParse(work.SourceId, out var eventId))
        {
            var prefix = $"agent-ticket-claim:{eventId:N}:";
            var claimed = await db.WorkItemActivities.AsNoTracking().Where(x => x.OrganizationId == org &&
                x.ActorSubjectId == work.AgentInstallationId && x.ActorKind == Domain.Security.GrantSubjectKind.AgentInstallation &&
                x.EventType == "agent.ticket.claimed" && x.OccurredAt <= at && x.IdempotencyKey != null &&
                x.IdempotencyKey.StartsWith(prefix)).Select(x => x.WorkItemId).Distinct().Take(2).ToListAsync(ct);
            if (claimed.Count == 1)
            {
                var root = await db.CoreWorkTasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == claimed[0] && x.OrganizationId == org, ct);
                // Without a dispatch snapshot, a plan's child focus is ambiguous. A simple
                // claimed commitment has an exact source even when it is now blocked/completed.
                if (root is not null && CSweetDbContext.PlanRoot(root) is null)
                { itemId = root.Id; kind = "Claim"; }
            }
        }
        if (itemId is null && log.TaskRunId is { } taskRun)
        {
            itemId = await db.CoreTaskRuns.Where(x => x.Id == taskRun && x.Task!.OrganizationId == org)
                .Select(x => (Guid?)x.TaskId).SingleOrDefaultAsync(ct);
            kind = "TaskRun";
        }
        if (itemId is { } id)
        {
            var item = await db.CoreWorkTasks.AsNoTracking().Include(x => x.Board)
                .SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == org && x.CreatedAt <= at, ct);
            if (item is not null)
            {
                log.WorkItemId = item.Id;
                // A changed historical hierarchy is not a trustworthy ancestry/project snapshot.
                if (!historical || item.UpdatedAt <= at)
                {
                    projectId ??= await db.ExecutionProjectAsync(org, item, ct);
                    var ancestors = new List<Guid>(); var seen = new HashSet<Guid> { item.Id };
                    var parent = item.ParentWorkTaskId;
                    while (parent is { } p && seen.Add(p) && ancestors.Count < 64)
                    {
                        var row = await db.CoreWorkTasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == p && x.OrganizationId == org, ct);
                        if (row is null || historical && row.UpdatedAt > at) break;
                        ancestors.Add(row.Id); parent = row.ParentWorkTaskId;
                    }
                    log.AncestorWorkItemIdsJson = JsonSerializer.Serialize(ancestors);
                }
            }
        }
        if (projectId is { } project && await db.Workstreams.AnyAsync(x => x.Id == project && x.OrganizationId == org, ct))
            log.WorkstreamId = project;
        if (log.WorkItemId.HasValue || log.WorkstreamId.HasValue)
            log.AttributionKind = historical ? kind + "Historical" : kind;
    }

    /// <summary>Completed receipts only: never race an active stream's final receipt write.</summary>
    public static async Task<int> BackfillAsync(CSweetDbContext db, Guid? organizationId = null, CancellationToken ct = default)
    {
        var changed = 0;
        Guid? after = null;
        while (true)
        {
            var rows = await db.AgentRunLogs.ProviderCalls().Where(x => x.CompletedAt != null &&
                x.WorkItemId == null && x.WorkstreamId == null && x.MemoryErasedAt == null &&
                (!organizationId.HasValue || x.OrganizationId == organizationId) &&
                (!after.HasValue || x.Id.CompareTo(after.Value) > 0))
                .OrderBy(x => x.Id).Take(256).ToListAsync(ct);
            if (rows.Count == 0) return changed;
            foreach (var row in rows)
            {
                await CaptureAsync(db, row, row.AgentWorkItemId, historical: true, ct);
                if (row.WorkItemId.HasValue || row.WorkstreamId.HasValue) changed++;
            }
            await db.SaveChangesAsync(ct);
            after = rows[^1].Id;
            foreach (var row in rows) db.Entry(row).State = EntityState.Detached;
        }
    }
}
