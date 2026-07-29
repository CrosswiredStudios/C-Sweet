using System.Text.Json;
using CSweet.Application.Security;
using CSweet.Application.Setup;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed class WorkAutomationService(
    CSweetDbContext db,
    IScopedActionAuthorizationService authorization,
    IAuditEventWriter audit) : IWorkAutomationService
{
    private static readonly HashSet<string> SupportedTriggers = new(StringComparer.Ordinal)
    {
        "item.created",
        "item.moved",
        "item.completed",
        "item.cancelled",
        "item.reopened",
        "item.sprint.assigned",
        "item.sprint.removed",
        "item.estimate.changed",
        "comment.created"
    };

    private static readonly HashSet<string> SupportedActions =
        new([WorkItemActions.Move, WorkItemActions.Complete,
            WorkItemActions.Cancel, WorkItemActions.Reopen], StringComparer.Ordinal);

    public async Task<WorkAutomationDirectoryResponse> ListAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default)
    {
        var member = await ResolveMemberAsync(organizationId, applicationUserId, cancellationToken);
        await RequireAsync(organizationId, boardId, member, WorkAutomationActions.Read, cancellationToken);
        if (!await db.WorkBoards.AnyAsync(x =>
                x.Id == boardId && x.OrganizationId == organizationId, cancellationToken))
            throw new KeyNotFoundException("Board was not found.");

        var rules = await db.WorkAutomationRules.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.BoardId == boardId)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
        var responses = new List<WorkAutomationRuleResponse>(rules.Count);
        foreach (var rule in rules)
            responses.Add(await ToResponseAsync(rule, cancellationToken));

        var executions = await db.WorkAutomationExecutions.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.BoardId == boardId)
            .OrderByDescending(x => x.CompletedAt)
            .Take(100)
            .Select(x => new WorkAutomationExecutionResponse(
                x.Id, x.RuleId, x.SourceActivityId, x.WorkItemId,
                x.Status.ToString(), x.RequiredAction,
                x.AuthorizingGrantId, x.AuthorizingGrantRevision,
                x.ErrorCode, x.ErrorMessage, x.CompletedAt))
            .ToListAsync(cancellationToken);
        return new WorkAutomationDirectoryResponse(responses, executions);
    }

    public async Task<WorkAutomationRuleResponse> CreateAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CreateWorkAutomationRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = await ResolveMemberAsync(organizationId, applicationUserId, cancellationToken);
        var decision = await RequireAsync(
            organizationId, boardId, member, WorkAutomationActions.Manage, cancellationToken);
        await ValidateAsync(
            organizationId, boardId, request.Name, request.TriggerEventType,
            request.ConditionColumnId, request.Action, request.TargetColumnId, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var ruleId = Guid.NewGuid();
        var rule = new WorkAutomationRule
        {
            Id = ruleId,
            OrganizationId = organizationId,
            BoardId = boardId,
            AutomationIdentityId = ruleId,
            Name = request.Name.Trim(),
            TriggerEventType = request.TriggerEventType.Trim(),
            ConditionColumnId = request.ConditionColumnId,
            Action = request.Action.Trim(),
            TargetColumnId = request.TargetColumnId,
            IsEnabled = request.IsEnabled,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.WorkAutomationRules.Add(rule);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, boardId, rule, member, decision,
            "created", cancellationToken);
        return await ToResponseAsync(rule, cancellationToken);
    }

    public async Task<WorkAutomationRuleResponse?> UpdateAsync(
        Guid organizationId,
        Guid boardId,
        Guid ruleId,
        Guid applicationUserId,
        UpdateWorkAutomationRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = await ResolveMemberAsync(organizationId, applicationUserId, cancellationToken);
        var decision = await RequireAsync(
            organizationId, boardId, member, WorkAutomationActions.Manage, cancellationToken);
        var rule = await db.WorkAutomationRules.SingleOrDefaultAsync(x =>
            x.Id == ruleId && x.OrganizationId == organizationId && x.BoardId == boardId,
            cancellationToken);
        if (rule is null) return null;
        if (rule.Revision != request.ExpectedRevision)
            throw new DbUpdateConcurrencyException("The automation rule changed since it was loaded.");
        await ValidateAsync(
            organizationId, boardId, request.Name, request.TriggerEventType,
            request.ConditionColumnId, request.Action, request.TargetColumnId, cancellationToken);

        rule.Name = request.Name.Trim();
        rule.TriggerEventType = request.TriggerEventType.Trim();
        rule.ConditionColumnId = request.ConditionColumnId;
        rule.Action = request.Action.Trim();
        rule.TargetColumnId = request.TargetColumnId;
        rule.IsEnabled = request.IsEnabled;
        rule.Revision++;
        rule.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, boardId, rule, member, decision,
            "updated", cancellationToken);
        return await ToResponseAsync(rule, cancellationToken);
    }

    public async Task<bool> DeleteAsync(
        Guid organizationId,
        Guid boardId,
        Guid ruleId,
        Guid applicationUserId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var member = await ResolveMemberAsync(organizationId, applicationUserId, cancellationToken);
        var decision = await RequireAsync(
            organizationId, boardId, member, WorkAutomationActions.Manage, cancellationToken);
        var rule = await db.WorkAutomationRules.SingleOrDefaultAsync(x =>
            x.Id == ruleId && x.OrganizationId == organizationId && x.BoardId == boardId,
            cancellationToken);
        if (rule is null) return false;
        if (rule.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException("The automation rule changed since it was loaded.");
        if (await db.WorkAutomationExecutions.AnyAsync(
                x => x.RuleId == ruleId, cancellationToken))
            throw new InvalidOperationException(
                "Rules with execution history cannot be deleted; disable the rule to preserve its audit evidence.");

        db.WorkAutomationRules.Remove(rule);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, boardId, rule, member, decision,
            "deleted", cancellationToken);
        return true;
    }

    private async Task ValidateAsync(
        Guid organizationId,
        Guid boardId,
        string name,
        string triggerEventType,
        Guid? conditionColumnId,
        string action,
        Guid targetColumnId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Automation rule name is required.");
        if (name.Trim().Length > 160)
            throw new ArgumentException("Automation rule name cannot exceed 160 characters.");
        if (!SupportedTriggers.Contains(triggerEventType?.Trim() ?? string.Empty))
            throw new ArgumentException("The automation trigger is not supported.");
        var normalizedAction = action?.Trim() ?? string.Empty;
        if (!SupportedActions.Contains(normalizedAction))
            throw new ArgumentException("The automation action is not supported.");

        var board = await db.WorkBoards.AsNoTracking()
            .Include(x => x.Columns)
            .SingleOrDefaultAsync(x =>
                x.Id == boardId && x.OrganizationId == organizationId &&
                x.ArchivedAt == null, cancellationToken)
            ?? throw new KeyNotFoundException("Board was not found.");
        if (conditionColumnId.HasValue &&
            board.Columns.All(x => x.Id != conditionColumnId.Value))
            throw new ArgumentException("The condition column does not belong to this board.");
        var target = board.Columns.SingleOrDefault(x => x.Id == targetColumnId)
            ?? throw new ArgumentException("The target column does not belong to this board.");
        var validTarget = normalizedAction switch
        {
            WorkItemActions.Complete => target.Category == WorkBoardColumnCategory.Done,
            WorkItemActions.Cancel => target.Category == WorkBoardColumnCategory.Cancelled,
            WorkItemActions.Move or WorkItemActions.Reopen =>
                target.Category is WorkBoardColumnCategory.ToDo or WorkBoardColumnCategory.InProgress,
            _ => false
        };
        if (!validTarget)
            throw new ArgumentException("The target column is incompatible with the selected action.");
    }

    private async Task<WorkAutomationRuleResponse> ToResponseAsync(
        WorkAutomationRule rule,
        CancellationToken cancellationToken)
    {
        var grant = await authorization.AuthorizeAsync(
            rule.OrganizationId, GrantSubjectKind.AutomationIdentity,
            rule.AutomationIdentityId, rule.Action,
            GrantScopeKind.Board, rule.BoardId, cancellationToken);
        return new WorkAutomationRuleResponse(
            rule.Id, rule.BoardId, rule.AutomationIdentityId, rule.Name,
            rule.TriggerEventType, rule.ConditionColumnId, rule.Action,
            rule.TargetColumnId, rule.IsEnabled, grant.Allowed,
            rule.Revision, rule.CreatedAt, rule.UpdatedAt);
    }

    private async Task<OrganizationUser> ResolveMemberAsync(
        Guid organizationId,
        Guid applicationUserId,
        CancellationToken cancellationToken) =>
        await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.ApplicationUserId == applicationUserId &&
            x.EmployeeType == EmployeeType.Human &&
            x.IsActive, cancellationToken)
        ?? throw new UnauthorizedAccessException(
            "The current user is not an active human member of this organization.");

    private async Task<ScopedAuthorizationDecision> RequireAsync(
        Guid organizationId,
        Guid boardId,
        OrganizationUser member,
        string action,
        CancellationToken cancellationToken)
    {
        await WorkBoardProvisioning.EnsureLegacyGrantsAsync(
            db, organizationId, member, cancellationToken);
        var decision = await authorization.AuthorizeAsync(
            organizationId, GrantSubjectKind.OrganizationUser, member.Id,
            action, GrantScopeKind.Board, boardId, cancellationToken);
        if (decision.Allowed) return decision;
        await audit.AppendAsync(new AuditEventWriteRequest(
            "work.authorization.denied", "WorkManagement", "Inbound", "Denied",
            organizationId, "WorkBoard", boardId, $"Denied {action}.",
            MetadataJson: JsonSerializer.Serialize(new { action, boardId }),
            Actor: new AuditActor(
                "Human", true, member.ApplicationUserId, member.Id, member.DisplayName),
            ErrorCode: "grant_required"), cancellationToken);
        throw new UnauthorizedAccessException(
            $"The current user does not have the required '{action}' grant.");
    }

    private Task WriteAuditAsync(
        Guid organizationId,
        Guid boardId,
        WorkAutomationRule rule,
        OrganizationUser member,
        ScopedAuthorizationDecision decision,
        string operation,
        CancellationToken cancellationToken) =>
        audit.AppendAsync(new AuditEventWriteRequest(
            WorkAutomationActions.Manage, "WorkManagement", "Inbound", "Completed",
            organizationId, "WorkAutomationRule", rule.Id,
            $"Automation rule {operation}.",
            MetadataJson: JsonSerializer.Serialize(new
            {
                operation,
                boardId,
                rule.AutomationIdentityId,
                rule.TriggerEventType,
                rule.Action,
                decision.GrantId,
                decision.GrantRevision
            }),
            Actor: new AuditActor(
                "Human", true, member.ApplicationUserId, member.Id, member.DisplayName)),
            cancellationToken);
}
