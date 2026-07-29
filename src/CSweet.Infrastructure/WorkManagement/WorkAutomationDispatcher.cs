using System.Text.Json;
using CSweet.Application.Security;
using CSweet.Application.Setup;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.Realtime;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Notifications;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed class WorkAutomationDispatcher(
    CSweetDbContext db,
    IScopedActionAuthorizationService authorization,
    IAuditEventWriter audit) : IWorkAutomationDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<int> DispatchBatchAsync(
        int batchSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        var rules = await db.WorkAutomationRules.AsNoTracking()
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        var processed = 0;
        foreach (var rule in rules)
        {
            if (processed >= batchSize) break;
            var activityIds = await db.WorkItemActivities.AsNoTracking()
                .Where(x =>
                    x.OrganizationId == rule.OrganizationId &&
                    x.BoardId == rule.BoardId &&
                    x.EventType == rule.TriggerEventType &&
                    x.ActorKind != GrantSubjectKind.AutomationIdentity &&
                    x.OccurredAt >= rule.CreatedAt &&
                    !db.WorkAutomationExecutions.Any(execution =>
                        execution.RuleId == rule.Id &&
                        execution.SourceActivityId == x.Id))
                .OrderBy(x => x.OccurredAt)
                .Select(x => x.Id)
                .Take(batchSize - processed)
                .ToListAsync(cancellationToken);
            foreach (var activityId in activityIds)
            {
                await ExecuteAsync(rule.Id, activityId, cancellationToken);
                processed++;
            }
        }
        return processed;
    }

    private async Task ExecuteAsync(
        Guid ruleId,
        Guid activityId,
        CancellationToken cancellationToken)
    {
        var rule = await db.WorkAutomationRules.SingleOrDefaultAsync(
            x => x.Id == ruleId, cancellationToken);
        var activity = await db.WorkItemActivities.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == activityId, cancellationToken);
        if (rule is null || activity is null || !rule.IsEnabled) return;
        if (await db.WorkAutomationExecutions.AnyAsync(
                x => x.RuleId == ruleId && x.SourceActivityId == activityId,
                cancellationToken))
            return;

        var now = DateTimeOffset.UtcNow;
        var execution = new WorkAutomationExecution
        {
            Id = Guid.NewGuid(),
            OrganizationId = rule.OrganizationId,
            BoardId = rule.BoardId,
            RuleId = rule.Id,
            SourceActivityId = activity.Id,
            WorkItemId = activity.WorkItemId,
            Status = WorkAutomationExecutionStatus.Pending,
            RequiredAction = rule.Action,
            CreatedAt = now,
            CompletedAt = now
        };
        db.WorkAutomationExecutions.Add(execution);

        var item = await db.CoreWorkTasks.SingleOrDefaultAsync(x =>
            x.Id == activity.WorkItemId &&
            x.OrganizationId == rule.OrganizationId &&
            x.BoardId == rule.BoardId, cancellationToken);
        if (item is null)
        {
            Complete(execution, WorkAutomationExecutionStatus.Skipped,
                "item_unavailable", "The work item is no longer on this board.");
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        if (rule.ConditionColumnId.HasValue &&
            item.BoardColumnId != rule.ConditionColumnId)
        {
            Complete(execution, WorkAutomationExecutionStatus.Skipped,
                "condition_not_met", "The work item is not in the rule's condition column.");
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        if (item.BoardColumnId == rule.TargetColumnId)
        {
            Complete(execution, WorkAutomationExecutionStatus.Skipped,
                "already_in_target", "The work item is already in the target column.");
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var target = await db.WorkBoardColumns.SingleOrDefaultAsync(x =>
            x.Id == rule.TargetColumnId && x.BoardId == rule.BoardId,
            cancellationToken);
        if (target is null || !IsValidTransition(item, target, rule.Action))
        {
            Complete(execution, WorkAutomationExecutionStatus.Skipped,
                "transition_not_applicable",
                "The configured action is not valid for the work item's current state.");
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var decision = await authorization.AuthorizeAsync(
            rule.OrganizationId, GrantSubjectKind.AutomationIdentity,
            rule.AutomationIdentityId, rule.Action,
            GrantScopeKind.Board, rule.BoardId, cancellationToken);
        if (!decision.Allowed)
        {
            Complete(execution, WorkAutomationExecutionStatus.Denied,
                "grant_required",
                $"Automation identity {rule.AutomationIdentityId:D} lacks '{rule.Action}'.");
            await db.SaveChangesAsync(cancellationToken);
            await WriteAuditAsync(
                rule, execution, decision, "Denied", cancellationToken);
            return;
        }

        if (target.WipPolicy == WorkBoardWipPolicy.HardLimit &&
            target.WipLimit.HasValue)
        {
            var targetCount = await db.CoreWorkTasks.CountAsync(x =>
                x.BoardColumnId == target.Id && x.Id != item.Id, cancellationToken);
            if (targetCount >= target.WipLimit.Value)
            {
                Complete(execution, WorkAutomationExecutionStatus.Failed,
                    "wip_limit", $"The target column WIP limit of {target.WipLimit.Value} was reached.");
                execution.AuthorizingGrantId = decision.GrantId;
                execution.AuthorizingGrantRevision = decision.GrantRevision;
                await db.SaveChangesAsync(cancellationToken);
                await WriteAuditAsync(
                    rule, execution, decision, "Failed", cancellationToken);
                return;
            }
        }

        var sourceColumnId = item.BoardColumnId;
        item.BoardColumnId = target.Id;
        item.BoardRank = (await db.CoreWorkTasks
            .Where(x => x.BoardColumnId == target.Id && x.Id != item.Id)
            .MaxAsync(x => (long?)x.BoardRank, cancellationToken) ?? 0) + 1024;
        item.Status = StatusFor(target.Category);
        item.Revision++;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        execution.Status = WorkAutomationExecutionStatus.Succeeded;
        execution.AuthorizingGrantId = decision.GrantId;
        execution.AuthorizingGrantRevision = decision.GrantRevision;
        execution.CompletedAt = item.UpdatedAt;
        var eventType = EventTypeFor(rule.Action);
        db.WorkItemActivities.Add(new WorkItemActivity
        {
            Id = Guid.NewGuid(),
            OrganizationId = rule.OrganizationId,
            BoardId = rule.BoardId,
            WorkItemId = item.Id,
            EventType = eventType,
            Action = rule.Action,
            ActorKind = GrantSubjectKind.AutomationIdentity,
            ActorSubjectId = rule.AutomationIdentityId,
            ActorDisplayName = rule.Name,
            AuthorizingGrantId = decision.GrantId,
            AuthorizingGrantRevision = decision.GrantRevision,
            IdempotencyKey = $"automation:{rule.Id:D}:{activity.Id:D}",
            DataJson = JsonSerializer.Serialize(new
            {
                sourceColumnId,
                targetColumnId = target.Id,
                item.BoardRank,
                ruleId = rule.Id,
                sourceActivityId = activity.Id
            }, JsonOptions),
            OccurredAt = item.UpdatedAt
        });
        await WorkSprintMetricsRecorder.RecordAsync(
            db, item.SprintId, eventType, item.UpdatedAt, cancellationToken);
        await QueueRealtimeAsync(
            rule.OrganizationId, rule.BoardId, item.Id,
            eventType, item.Revision, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            rule, execution, decision, "Completed", cancellationToken);
    }

    private async Task QueueRealtimeAsync(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        string changeType,
        long revision,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var grants = await db.ScopedActionGrants.AsNoTracking().Where(x =>
                x.OrganizationId == organizationId &&
                x.SubjectKind == GrantSubjectKind.OrganizationUser &&
                x.RevokedAt == null &&
                (!x.ExpiresAt.HasValue || x.ExpiresAt > now) &&
                (x.ScopeKind == GrantScopeKind.Organization ||
                 (x.ScopeKind == GrantScopeKind.Board && x.ScopeId == boardId)) &&
                (x.Action == WorkBoardActions.Read ||
                 x.Action == WorkItemActions.Read))
            .Select(x => new { x.SubjectId, x.Action })
            .ToListAsync(cancellationToken);
        var boardReaders = grants.Where(x => x.Action == WorkBoardActions.Read)
            .Select(x => x.SubjectId).ToHashSet();
        var itemReaders = grants.Where(x => x.Action == WorkItemActions.Read)
            .Select(x => x.SubjectId).ToHashSet();
        boardReaders.IntersectWith(itemReaders);
        var recipients = await db.CoreOrganizationUsers.AsNoTracking().Where(x =>
                boardReaders.Contains(x.Id) &&
                x.OrganizationId == organizationId &&
                x.EmployeeType == EmployeeType.Human &&
                x.IsActive)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        db.ApplicationRealtimeOutbox.Add(new ApplicationRealtimeOutboxItem
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            RecipientOrganizationUserIdsJson =
                JsonSerializer.Serialize(recipients, JsonOptions),
            EventType = AppRealtimeEvents.WorkBoardChanged,
            Subject = $"organizations/{organizationId:D}/work/boards/{boardId:D}",
            DataJson = JsonSerializer.Serialize(new
            {
                boardId,
                itemId,
                changeType,
                revision
            }, JsonOptions),
            Status = ApplicationRealtimeOutboxStatus.Pending,
            NextAttemptAt = now,
            OccurredAt = now
        });
    }

    private Task WriteAuditAsync(
        WorkAutomationRule rule,
        WorkAutomationExecution execution,
        ScopedAuthorizationDecision decision,
        string outcome,
        CancellationToken cancellationToken) =>
        audit.AppendAsync(new AuditEventWriteRequest(
            "work.automation.executed", "WorkManagement", "Internal", outcome,
            rule.OrganizationId, "WorkAutomationExecution", execution.Id,
            $"Automation rule '{rule.Name}' {outcome.ToLowerInvariant()}.",
            MetadataJson: JsonSerializer.Serialize(new
            {
                ruleId = rule.Id,
                rule.AutomationIdentityId,
                execution.SourceActivityId,
                execution.WorkItemId,
                execution.RequiredAction,
                status = execution.Status.ToString(),
                decision.GrantId,
                decision.GrantRevision,
                execution.ErrorCode
            }),
            Actor: new AuditActor("Automation", true, DisplayName: rule.Name),
            ErrorCode: execution.ErrorCode,
            ErrorMessage: execution.ErrorMessage),
            cancellationToken);

    private static void Complete(
        WorkAutomationExecution execution,
        WorkAutomationExecutionStatus status,
        string errorCode,
        string message)
    {
        execution.Status = status;
        execution.ErrorCode = errorCode;
        execution.ErrorMessage = message;
        execution.CompletedAt = DateTimeOffset.UtcNow;
    }

    private static bool IsValidTransition(
        WorkTask item,
        WorkBoardColumn target,
        string action) =>
        action switch
        {
            WorkItemActions.Complete =>
                item.Status != WorkTaskStatus.Completed &&
                target.Category == WorkBoardColumnCategory.Done,
            WorkItemActions.Cancel =>
                item.Status != WorkTaskStatus.Cancelled &&
                target.Category == WorkBoardColumnCategory.Cancelled,
            WorkItemActions.Reopen =>
                item.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled &&
                target.Category is WorkBoardColumnCategory.ToDo or WorkBoardColumnCategory.InProgress,
            WorkItemActions.Move =>
                item.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled) &&
                target.Category is WorkBoardColumnCategory.ToDo or WorkBoardColumnCategory.InProgress,
            _ => false
        };

    private static WorkTaskStatus StatusFor(WorkBoardColumnCategory category) =>
        category switch
        {
            WorkBoardColumnCategory.Done => WorkTaskStatus.Completed,
            WorkBoardColumnCategory.Cancelled => WorkTaskStatus.Cancelled,
            WorkBoardColumnCategory.InProgress => WorkTaskStatus.Running,
            _ => WorkTaskStatus.Ready
        };

    private static string EventTypeFor(string action) =>
        action switch
        {
            WorkItemActions.Complete => "item.completed",
            WorkItemActions.Cancel => "item.cancelled",
            WorkItemActions.Reopen => "item.reopened",
            _ => "item.moved"
        };
}
