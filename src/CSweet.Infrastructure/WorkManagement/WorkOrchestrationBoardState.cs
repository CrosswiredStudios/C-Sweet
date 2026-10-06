using System.Text.Json;
using CSweet.Contracts.Realtime;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Notifications;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;
using CSweet.Infrastructure.Persistence;
using CSweet.Domain.Setup;

namespace CSweet.Infrastructure.WorkManagement;

internal static partial class WorkOrchestrationBoardState
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    // Called inside the inbox lease transaction, before the worker receives its payload.
    // Execution authorization must not wait for the next scheduler reconciliation.
    internal static async Task RecordClaimAsync(CSweetDbContext db, AgentWorkItem work,
        DateTimeOffset now, CancellationToken token)
    {
        if (work.SourceType == "WorkDeliveryStage")
        {
            var deliveryAttempt = await db.WorkExecutionAttempts.Include(x => x.StageExecution)!
                .ThenInclude(x => x!.DeliveryExecution)!.ThenInclude(x => x!.Plan)
                .SingleOrDefaultAsync(x => x.AgentWorkItemId == work.Id, token);
            var deliveryStage = deliveryAttempt?.StageExecution;
            var delivery = deliveryStage?.DeliveryExecution;
            if (delivery?.Plan is not { Status: "Active" } plan || plan.OrganizationId.ToString() != work.OrganizationId ||
                delivery.ScopeRevision != plan.ScopeRevision || delivery.CurrentStageKey != deliveryStage!.StageKey ||
                deliveryStage.AgentInstallationId != work.AgentInstallationId || delivery.Status != "Running" ||
                deliveryStage.Status is not (WorkStageExecutionStatus.Dispatching or WorkStageExecutionStatus.Running))
                throw new UnauthorizedAccessException("The delivery plan or authoritative review assignment is no longer active.");
            deliveryAttempt!.Status = WorkExecutionAttemptStatus.Running; deliveryAttempt.StartedAt ??= now;
            deliveryStage.Status = WorkStageExecutionStatus.Running; deliveryStage.UpdatedAt = now;
            await db.SaveChangesAsync(token);
            return;
        }
        if (work.SourceType != "WorkStageExecution") return;
        var attempt = await db.WorkExecutionAttempts
            .Include(x => x.StageExecution)!.ThenInclude(x => x!.ItemExecution)!.ThenInclude(x => x!.WorkItem)
            .Include(x => x.StageExecution)!.ThenInclude(x => x!.ItemExecution)!.ThenInclude(x => x!.SprintExecution)
            .SingleOrDefaultAsync(x => x.AgentWorkItemId == work.Id, token);
        if (attempt?.StageExecution is not { } stage) return;
        var execution = stage.ItemExecution!.SprintExecution!;
        if (execution.OrganizationId.ToString() != work.OrganizationId ||
            execution.Status != WorkSprintExecutionStatus.Active || stage.AgentInstallationId != work.AgentInstallationId ||
            stage.StageKey != stage.ItemExecution.CurrentStageKey ||
            stage.Status is not (WorkStageExecutionStatus.Dispatching or WorkStageExecutionStatus.Running)) return;
        attempt.Status = WorkExecutionAttemptStatus.Running;
        attempt.StartedAt ??= now;
        stage.Status = WorkStageExecutionStatus.Running;
        stage.UpdatedAt = now;
        stage.ItemExecution.Status = WorkItemExecutionStatus.Running;
        stage.ItemExecution.UpdatedAt = now;
        var policy = await db.WorkOrchestrationPolicyRevisions.AsNoTracking()
            .Include(x => x.Stages).Include(x => x.Transitions)
            .SingleAsync(x => x.Id == execution.PolicyRevisionId, token);
        SynchronizeAgentCard(policy, stage, now);
        await SaveBoardChangesAsync(db, execution, now, token);
    }

    internal static void SynchronizeAgentCard(
        WorkOrchestrationPolicyRevision policy, WorkStageExecution stage, DateTimeOffset now)
    {
        if (stage.StageType != WorkOrchestrationStageType.AgentExecution &&
            !(stage.StageType == WorkOrchestrationStageType.MemberExecution &&
              stage.PrincipalKind == WorkOrchestrationPrincipalKind.AgentInstallation)) return;
        if (stage.Status is not (WorkStageExecutionStatus.Pending or WorkStageExecutionStatus.Dispatching or
            WorkStageExecutionStatus.Backoff or WorkStageExecutionStatus.Running)) return;

        var item = stage.ItemExecution!.WorkItem!;
        var definition = policy.Stages.Single(x => x.Key == stage.StageKey);
        var running = stage.Status == WorkStageExecutionStatus.Running;
        // Queue successors stay queued until claimed. Later review stages retain their
        // own columns, so waiting for QA does not look like new development.
        var queueColumn = policy.Stages
            .Where(x => x.Type == WorkOrchestrationStageType.Queue && x.ColumnId.HasValue &&
                policy.Transitions.Any(t => t.FromStageKey == x.Key && t.ToStageKey == stage.StageKey))
            .OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.ColumnId).FirstOrDefault();
        var column = (running ? definition.ColumnId : queueColumn ?? definition.ColumnId) ?? item.BoardColumnId;
        var status = running ? WorkTaskStatus.Running : WorkTaskStatus.Ready;
        if (item.BoardColumnId == column && item.Status == status) return;
        item.BoardColumnId = column;
        item.Status = status;
        item.UpdatedAt = now;
        item.Revision++;
    }

    /// <summary>
    /// A stage that needs a person (Blocked, or Failed with its automatic retries spent) parks its card in the
    /// board's Blocked column, creating the column when the board has none. The board then shows what the
    /// ticket needs, and moving the card back to a ready column retries it.
    /// </summary>
    internal static async Task ParkBlockedCardsAsync(
        CSweetDbContext db, WorkSprintExecution execution, DateTimeOffset now, CancellationToken token)
    {
        var parked = execution.Items.Where(x => x.WorkItem is not null &&
                x.Stages.OrderByDescending(s => s.CreatedAt).FirstOrDefault() is
                    { Status: WorkStageExecutionStatus.Blocked or WorkStageExecutionStatus.Failed })
            .ToList();
        if (parked.Count == 0) return;
        var column = await WorkBoardBlockedColumn.EnsureAsync(db, execution.BoardId, now, token);
        foreach (var item in parked)
        {
            var card = item.WorkItem!;
            if (card.BoardColumnId == column.Id) continue;
            card.BoardColumnId = column.Id;
            if (card.Status is not (WorkTaskStatus.Blocked or WorkTaskStatus.Failed)) card.Status = WorkTaskStatus.Blocked;
            card.BlockReason ??= item.BlockedReason;
            card.UpdatedAt = now;
            card.Revision++;
        }
    }

    internal static async Task SaveBoardChangesAsync(CSweetDbContext db, WorkSprintExecution execution, DateTimeOffset now, CancellationToken token)
    {
        await SynchronizeTaskArtifactGrantsAsync(db, execution, now, token);
        db.ChangeTracker.DetectChanges();
        var completedDeliveryStages = db.ChangeTracker.Entries<WorkStageExecution>().Where(x =>
            x.Entity.ItemExecution?.SprintExecutionId == execution.Id && x.Entity.Status == WorkStageExecutionStatus.Completed &&
            (x.State == EntityState.Added || x.Property(s => s.Status).IsModified)).Select(x => x.Entity).ToList();
        foreach (var stage in completedDeliveryStages)
        {
            var task = stage.ItemExecution!.WorkItem!;
            if (task.DeliverySpecificationJson is not { } deliveryJson ||
                JsonSerializer.Deserialize<CSweet.WorkManagement.Contracts.WorkItemDeliverySpecification>(deliveryJson, JsonOptions)?.DeliveryPlanId is not { } planId) continue;
            var plan = await db.WorkDeliveryPlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == planId && x.OrganizationId == execution.OrganizationId, token);
            if (plan is null) continue;
            var wake = new CSweet.WorkManagement.Contracts.GenericResourceEvent(Guid.NewGuid(), now,
                new(execution.OrganizationId, plan.WorkstreamId, null, execution.BoardId, task.Id, null, null, execution.Id, null, null),
                "WorkDeliveryPlan", planId, plan.Revision, "project-delivery", $"task.{stage.StageKey}.{stage.LastOutcomeCode}",
                JsonSerializer.SerializeToElement(new { stageId = stage.Id, itemId = task.Id, scopeRevision = plan.ScopeRevision }, JsonOptions));
            db.AgentPlatformEventOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = execution.OrganizationId,
                EventType = CSweet.WorkManagement.Contracts.WorkDeliveryCapabilities.Changed, DataJson = JsonSerializer.Serialize(wake, JsonOptions),
                IdempotencyKey = $"delivery-task-stage:{stage.Id:N}:{stage.Attempts.Count}:{stage.UpdatedAt.UtcTicks}", OccurredAt = now, NextAttemptAt = now });
        }
        var stopped = db.ChangeTracker.Entries<WorkStageExecution>().Where(x =>
            x.Entity.ItemExecution?.SprintExecutionId == execution.Id &&
            x.Entity.Status is WorkStageExecutionStatus.Blocked or WorkStageExecutionStatus.Failed &&
            (x.State == EntityState.Added || x.Property(s => s.Status).IsModified)).Select(x => x.Entity).ToList();
        if (stopped.Count > 0)
        {
            var board = await db.WorkBoards.AsNoTracking().SingleAsync(x => x.Id == execution.BoardId, token);
            foreach (var stage in stopped)
            {
                if (board.WorkstreamId is not { } projectId) continue;
                var wake = new CSweet.WorkManagement.Contracts.GenericResourceEvent(Guid.NewGuid(), now,
                    new(execution.OrganizationId, projectId, board.TeamId, board.Id,
                        stage.ItemExecution!.WorkItemId, null, null, execution.Id, null, null),
                    "WorkItem", stage.ItemExecution.WorkItemId, stage.ItemExecution.WorkItem!.Revision,
                    stage.ItemExecution.WorkItem.TypeKey, "stage.recovery-required",
                    JsonSerializer.SerializeToElement(new { stageId = stage.Id, stage.StageKey, stage.LastError }, JsonOptions));
                db.AgentPlatformEventOutbox.Add(new()
                {
                    Id = Guid.NewGuid(), OrganizationId = execution.OrganizationId,
                    EventType = CSweet.WorkManagement.Contracts.WorkstreamEventNames.WorkItemChangedV1,
                    DataJson = JsonSerializer.Serialize(wake, JsonOptions),
                    IdempotencyKey = $"stage-recovery:{stage.Id:N}:{stage.Attempts.Count}:{stage.UpdatedAt.UtcTicks}",
                    OccurredAt = now, NextAttemptAt = now
                });
            }
        }
        var changed = db.ChangeTracker.Entries<WorkTask>().Where(x =>
            x.Entity.BoardId == execution.BoardId && x.State == EntityState.Modified &&
            (x.Property(p => p.Status).IsModified || x.Property(p => p.BoardColumnId).IsModified || x.Property(p => p.Revision).IsModified))
            .Select(x => x.Entity).ToList();
        if (changed.Count > 0)
        {

            var grants = await db.ScopedActionGrants.AsNoTracking().Where(x =>
                x.OrganizationId == execution.OrganizationId && x.SubjectKind == GrantSubjectKind.OrganizationUser &&
                x.RevokedAt == null &&
                (x.ScopeKind == GrantScopeKind.Organization ||
                 (x.ScopeKind == GrantScopeKind.Board && x.ScopeId == execution.BoardId)) &&
                (x.Action == WorkBoardActions.Read || x.Action == WorkItemActions.Read))
                .Select(x => new { x.SubjectId, x.Action, x.ExpiresAt }).ToListAsync(token);
            grants = grants.Where(x => !x.ExpiresAt.HasValue || x.ExpiresAt > now).ToList();
            var readers = grants.Where(x => x.Action == WorkBoardActions.Read).Select(x => x.SubjectId)
                .Intersect(grants.Where(x => x.Action == WorkItemActions.Read).Select(x => x.SubjectId)).ToList();
            var recipients = await db.CoreOrganizationUsers.AsNoTracking().Where(x =>
                readers.Contains(x.Id) && x.OrganizationId == execution.OrganizationId &&
                x.EmployeeType == EmployeeType.Human && x.IsActive).Select(x => x.Id).ToListAsync(token);
            foreach (var item in changed)
                db.ApplicationRealtimeOutbox.Add(new ApplicationRealtimeOutboxItem
                {
                    Id = Guid.NewGuid(), OrganizationId = execution.OrganizationId,
                    RecipientOrganizationUserIdsJson = JsonSerializer.Serialize(recipients, JsonOptions),
                    EventType = AppRealtimeEvents.WorkBoardChanged,
                    Subject = $"organizations/{execution.OrganizationId:D}/work/boards/{execution.BoardId:D}",
                    DataJson = JsonSerializer.Serialize(new { boardId = execution.BoardId, itemId = item.Id,
                        changeType = "item.updated", revision = item.Revision }, JsonOptions),
                    Status = ApplicationRealtimeOutboxStatus.Pending, NextAttemptAt = now, OccurredAt = now
                });
        }
        await db.SaveChangesAsync(token);
    }
}
