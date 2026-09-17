using System.Text.Json;
using CSweet.Domain.Analytics;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    // This runs before SaveChanges: the source mutation, focus, intervals and existing
    // task/agent outbox notifications are committed by the same database transaction.
    private async Task CaptureWorkExecutionAsync(CancellationToken ct = default)
    {
        var attempts = ChangeTracker.Entries<AgentWorkAttempt>()
            .Where(x => x.State is EntityState.Added or EntityState.Modified).Select(x => x.Entity).ToList();
        var queuedWorkIds = ChangeTracker.Entries<AgentRunLog>().Where(x =>
            x.State is EntityState.Added or EntityState.Modified && x.Entity.MeasurementKind == "Queue" &&
            x.Entity.AgentWorkItemId.HasValue).Select(x => x.Entity.AgentWorkItemId!.Value).Distinct().ToArray();
        if (queuedWorkIds.Length > 0)
            attempts.AddRange(await AgentWorkAttempts.Where(x => queuedWorkIds.Contains(x.AgentWorkItemId) &&
                x.FinishedAt == null).ToListAsync(ct));
        var changedTasks = ChangeTracker.Entries<WorkTask>().Where(x =>
            x.State is EntityState.Added or EntityState.Modified).Select(x => x.Entity).ToArray();
        var roots = changedTasks.Select(x => PlanRoot(x) ?? x.Id)
            .Concat(ChangeTracker.Entries<WorkLifecycleEvent>().Where(x => x.State == EntityState.Added &&
                x.Entity.ResourceKind == "WorkItem").Select(x => x.Entity.ResourceId)).Distinct().ToArray();
        if (roots.Length > 0)
        {
            var claims = CoreWorkTasks.Local.Where(x => roots.Contains(x.Id) && x.ClaimEventId.HasValue)
                .Select(x => x.ClaimEventId!.Value.ToString()).ToArray();
            attempts.AddRange(await AgentWorkAttempts.Include(x => x.AgentWorkItem).Where(x => x.FinishedAt == null &&
                (WorkExecutionContexts.Any(c => c.Id == x.Id && c.RootWorkItemId.HasValue && roots.Contains(c.RootWorkItemId.Value)) ||
                 claims.Contains(x.AgentWorkItem!.SourceId!))).ToListAsync(ct));
        }
        foreach (var attempt in attempts.DistinctBy(x => x.Id))
        {
            var work = attempt.AgentWorkItem ?? await AgentWorkItems.FindAsync([attempt.AgentWorkItemId], ct);
            if (work is null || !Guid.TryParse(work.OrganizationId, out var org)) continue;
            var context = await WorkExecutionContexts.FindAsync([attempt.Id], ct);
            var root = await AgentTicketFeedback.ResolveAsync(this, work, ct);
            if (context is null)
            {
                context = new WorkExecutionContext { Id = attempt.Id, OrganizationId = org,
                    AgentInstallationId = work.AgentInstallationId, AgentWorkItemId = work.Id };
                WorkExecutionContexts.Add(context);
            }
            // Keep the original root so release/reassignment also closes the previous interval.
            root ??= context.RootWorkItemId is { } rootId ? await CoreWorkTasks.FindAsync([rootId], ct) : null;
            var now = ExecutionClock.GetUtcNow();
            var oldLease = Entry(attempt).State == EntityState.Added ? attempt.LeaseExpiresAt
                : Entry(attempt).Property(x => x.LeaseExpiresAt).OriginalValue;
            var expired = oldLease <= now || attempt.Error == "lease_expired" ||
                root?.Board?.Kind == WorkBoardKind.Personal && root.ClaimExpiresAt <= now;
            var running = !expired && attempt.FinishedAt is null && work.Status == AgentWorkStatus.Leased;
            var queueRows = await AgentRunLogs.Where(x => x.AgentWorkItemId == work.Id && x.MeasurementKind == "Queue" &&
                x.StartedAt >= attempt.ClaimedAt && x.CompletedAt == null).ToListAsync(ct);
            var queued = queueRows.Concat(AgentRunLogs.Local.Where(x => x.AgentWorkItemId == work.Id &&
                x.MeasurementKind == "Queue" && x.StartedAt >= attempt.ClaimedAt)).DistinctBy(x => x.Id)
                .Any(x => x.Status == "Queued" && x.CompletedAt == null);
            running &= !queued;
            WorkTask? focus = null;
            var executionAllowed = root is null;
            if (root is not null && root.OrganizationId == org && root.ArchivedAt is null &&
                root.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled or WorkTaskStatus.Blocked or WorkTaskStatus.WaitingForApproval))
            {
                root.Board ??= await WorkBoards.FindAsync([root.BoardId!], ct);
                if (root.Board is { ArchivedAt: null } && root.Board.Kind != WorkBoardKind.Personal)
                { focus = root; executionAllowed = true; }
                else if (root.Board is { ArchivedAt: null } && root.Status == WorkTaskStatus.Running && root.AssignedAgentInstallationId == work.AgentInstallationId &&
                    root.ClaimEventId?.ToString("D") == work.SourceId && root.ClaimExpiresAt > now &&
                    root.NextReviewAt is null && root.WaitingReason is null)
                {
                    // Load all candidates, including currently tracked status transitions. A SQL
                    // Running predicate would omit the child just started by this transaction.
                    var children = await CoreWorkTasks.Where(x => x.OrganizationId == org && x.BoardId == root.BoardId)
                        .ToListAsync(ct);
                    var active = children.Where(x => x.Id != root.Id && x.Kind == WorkItemKind.Task &&
                        x.ArchivedAt is null && x.Status == WorkTaskStatus.Running && PlanRoot(x) == root.Id).ToArray();
                    executionAllowed = true;
                    focus = active.Length == 0 ? root : active.Length == 1 &&
                        active[0].AssignedAgentInstallationId == work.AgentInstallationId && BelongsTo(active[0], root.Id, children)
                            ? active[0] : null;
                    // A blocked child suspends execution until it is explicitly resumed.
                    if (active.Length == 0 && children.Any(x => x.Kind == WorkItemKind.Task &&
                        x.Status == WorkTaskStatus.Blocked && PlanRoot(x) == root.Id))
                    { focus = null; executionAllowed = false; }
                }
            }
            var ancestors = focus is null ? [] : await ExecutionAncestorsAsync(org, focus, ct);
            var ancestryJson = JsonSerializer.Serialize(ancestors);
            var eligible = running && executionAllowed;
            var interval = WorkExecutionIntervals.Local.FirstOrDefault(x => x.AgentWorkAttemptId == attempt.Id && x.EndedAt is null)
                ?? await WorkExecutionIntervals.SingleOrDefaultAsync(x => x.AgentWorkAttemptId == attempt.Id && x.EndedAt == null, ct);
            // FindAsync/query can return an already modified tracked row. Never resurrect it.
            if (interval?.EndedAt is not null) interval = null;
            var confirmed = expired ? Entry(attempt).Property(x => x.LastConfirmedAt).OriginalValue ?? attempt.ClaimedAt
                : attempt.LastConfirmedAt ?? attempt.ClaimedAt;
            var taskChanged = root is not null && ChangeTracker.Entries<WorkTask>().Any(x =>
                x.State == EntityState.Modified && (x.Entity.Id == root.Id || PlanRoot(x.Entity) == root.Id) &&
                (x.Property(t => t.Status).IsModified || x.Property(t => t.ClaimEventId).IsModified ||
                 x.Property(t => t.NextReviewAt).IsModified || x.Property(t => t.ArchivedAt).IsModified ||
                 x.Property(t => t.ParentWorkTaskId).IsModified || x.Property(t => t.BoardId).IsModified));
            if (!expired && (taskChanged || queuedWorkIds.Contains(work.Id))) confirmed = now;
            if (!expired && attempt.FinishedAt is { } finish) confirmed = finish;
            if (interval is not null)
            {
                if (confirmed > interval.ConfirmedThrough) interval.ConfirmedThrough = confirmed;
                if (!eligible || focus?.Id != interval.WorkItemId || interval.AncestorWorkItemIdsJson != ancestryJson ||
                    interval.WorkstreamId != root?.Board?.WorkstreamId)
                {
                    interval.EndedAt = interval.ConfirmedThrough;
                    interval.EndReason = expired ? "LeaseLost" : attempt.FinishedAt.HasValue ? "AttemptEnded" : "FocusChangedOrPaused";
                }
            }
            if (eligible && (interval is null || interval.EndedAt is not null))
            {
                WorkExecutionIntervals.Add(new WorkExecutionInterval { Id = Guid.NewGuid(), OrganizationId = org,
                    AgentWorkAttemptId = attempt.Id, WorkItemId = focus?.Id, WorkstreamId = root?.Board?.WorkstreamId,
                    AncestorWorkItemIdsJson = ancestryJson, StartedAt = now, ConfirmedThrough = now });
            }
            context.RootWorkItemId = root?.Id;
            context.WorkItemId = running ? focus?.Id : null;
            // Serialize focus changes against concurrent heartbeats and plan reports. A losing
            // transaction rolls back all its intervals as well as the source mutation.
            context.Revision++;
        }
    }

    internal static Guid? PlanRoot(WorkTask item)
    {
        try { return JsonSerializer.Deserialize<CSweet.WorkManagement.Contracts.WorkItemPlanningSpecification>(
            item.PlanningSpecificationJson ?? "{}", new JsonSerializerOptions(JsonSerializerDefaults.Web))?.PersonalPlan?.RootItemId; }
        catch (JsonException) { return null; }
    }

    private static bool BelongsTo(WorkTask item, Guid root, IReadOnlyList<WorkTask> items)
    {
        var seen = new HashSet<Guid>();
        while (item.ParentWorkTaskId is { } parent && seen.Add(parent))
        {
            if (parent == root) return true;
            var next = items.FirstOrDefault(x => x.Id == parent);
            if (next is null) break;
            item = next;
        }
        return false;
    }

    internal async Task<List<Guid>> ExecutionAncestorsAsync(Guid organization, WorkTask item, CancellationToken ct)
    {
        var result = new List<Guid>(); var seen = new HashSet<Guid> { item.Id };
        while (item.ParentWorkTaskId is { } parent && seen.Add(parent) && result.Count < 64)
        {
            var next = await CoreWorkTasks.SingleOrDefaultAsync(x => x.Id == parent && x.OrganizationId == organization, ct);
            if (next is null) break;
            result.Add(parent); item = next;
        }
        return result;
    }
}
