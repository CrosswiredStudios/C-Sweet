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

public sealed class WorkItemCollaborationService(
    CSweetDbContext db,
    IScopedActionAuthorizationService authorization,
    IAuditEventWriter audit) : IWorkItemCollaborationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<WorkItemCollaborationResponse?> GetAsync(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default)
    {
        var member = await ResolveMemberAsync(organizationId, applicationUserId, cancellationToken);
        await RequireAsync(organizationId, boardId, member, WorkItemActions.Read, cancellationToken);
        if (!await db.CoreWorkTasks.AnyAsync(x =>
                x.Id == itemId && x.OrganizationId == organizationId && x.BoardId == boardId,
                cancellationToken))
            return null;
        var comments = await db.WorkItemComments.AsNoTracking()
            .Where(x => x.WorkItemId == itemId && x.DeletedAt == null)
            .OrderBy(x => x.CreatedAt)
            .Select(x => new WorkItemCommentResponse(
                x.Id, x.WorkItemId, x.AuthorKind.ToString(), x.AuthorSubjectId,
                x.AuthorDisplayName, x.Body, x.Revision, x.CreatedAt, x.EditedAt))
            .ToListAsync(cancellationToken);
        var activity = await db.WorkItemActivities.AsNoTracking()
            .Where(x => x.WorkItemId == itemId)
            .OrderByDescending(x => x.OccurredAt)
            .Take(200)
            .Select(x => new WorkItemActivityResponse(
                x.Id, x.BoardId, x.WorkItemId, x.EventType, x.Action,
                x.ActorKind.ToString(), x.ActorSubjectId, x.ActorDisplayName,
                x.DataJson, x.OccurredAt))
            .ToListAsync(cancellationToken);
        return new WorkItemCollaborationResponse(comments, activity);
    }

    public async Task<WorkItemCommentResponse?> AddCommentAsync(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        Guid applicationUserId,
        AddWorkItemCommentRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = await ResolveMemberAsync(organizationId, applicationUserId, cancellationToken);
        var decision = await RequireAsync(
            organizationId, boardId, member, WorkItemActions.Comment, cancellationToken);
        var item = await db.CoreWorkTasks.SingleOrDefaultAsync(x =>
            x.Id == itemId && x.OrganizationId == organizationId && x.BoardId == boardId,
            cancellationToken);
        if (item is null) return null;
        var body = request.Body?.Trim();
        ValidateMutation(body, request.IdempotencyKey, "Comment body");
        if (body!.Length > 8192)
            throw new ArgumentException("Comment body cannot exceed 8192 characters.");
        var existing = await db.WorkItemComments.AsNoTracking().SingleOrDefaultAsync(x =>
            x.WorkItemId == itemId &&
            x.AuthorKind == GrantSubjectKind.OrganizationUser &&
            x.AuthorSubjectId == member.Id &&
            x.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (existing is not null) return ToComment(existing);

        var now = DateTimeOffset.UtcNow;
        var comment = new WorkItemComment
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            WorkItemId = itemId,
            AuthorKind = GrantSubjectKind.OrganizationUser,
            AuthorSubjectId = member.Id,
            AuthorDisplayName = member.DisplayName,
            Body = body!,
            IdempotencyKey = request.IdempotencyKey.Trim(),
            CreatedAt = now
        };
        db.WorkItemComments.Add(comment);
        AddActivity(
            organizationId, boardId, itemId, member, WorkItemActions.Comment,
            "comment.created", decision, new { commentId = comment.Id }, now);
        await QueueRealtimeAsync(
            organizationId, boardId, itemId, "comment.created", item.Revision,
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, boardId, itemId, member, WorkItemActions.Comment,
            decision, new { commentId = comment.Id }, cancellationToken);
        return ToComment(comment);
    }

    public async Task<WorkBoardItemResponse?> TransferAsync(
        Guid organizationId,
        Guid sourceBoardId,
        Guid itemId,
        Guid applicationUserId,
        TransferWorkItemRequest request,
        CancellationToken cancellationToken = default)
    {
        if (sourceBoardId == request.TargetBoardId)
            throw new ArgumentException("Use a column move when the source and target board are the same.");
        var member = await ResolveMemberAsync(organizationId, applicationUserId, cancellationToken);
        var sourceDecision = await RequireAsync(
            organizationId, sourceBoardId, member, WorkItemActions.Transfer, cancellationToken);
        var targetDecision = await RequireAsync(
            organizationId, request.TargetBoardId, member, WorkItemActions.Transfer, cancellationToken);
        ValidateMutation("transfer", request.IdempotencyKey, "Transfer");
        var replay = await db.WorkItemActivities.AsNoTracking().SingleOrDefaultAsync(x =>
            x.ActorKind == GrantSubjectKind.OrganizationUser &&
            x.ActorSubjectId == member.Id &&
            x.Action == WorkItemActions.Transfer &&
            x.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (replay is not null && replay.WorkItemId != itemId)
            throw new InvalidOperationException(
                "The idempotency key was already used for a different work item transfer.");
        var item = await db.CoreWorkTasks.SingleOrDefaultAsync(x =>
            x.Id == itemId && x.OrganizationId == organizationId, cancellationToken);
        if (replay is not null)
            return item is null ? null : ToItem(item);
        if (item is null || item.BoardId != sourceBoardId) return null;
        if (item.Revision != request.ExpectedRevision)
            throw new DbUpdateConcurrencyException(
                $"Expected work item revision {request.ExpectedRevision}, current revision is {item.Revision}.");
        if (item.ParentWorkTaskId.HasValue ||
            await db.CoreWorkTasks.AnyAsync(x => x.ParentWorkTaskId == item.Id, cancellationToken))
            throw new InvalidOperationException(
                "A hierarchical work item must be detached or transferred with its hierarchy.");

        var targetBoard = await db.WorkBoards
            .Include(x => x.Columns)
            .SingleOrDefaultAsync(x =>
                x.Id == request.TargetBoardId &&
                x.OrganizationId == organizationId &&
                x.ArchivedAt == null, cancellationToken)
            ?? throw new KeyNotFoundException("Target board was not found.");
        var targetColumn = request.TargetColumnId.HasValue
            ? targetBoard.Columns.SingleOrDefault(x => x.Id == request.TargetColumnId.Value)
            : targetBoard.Columns.OrderBy(x => x.Position)
                .FirstOrDefault(x => x.Category == WorkBoardColumnCategory.ToDo);
        if (targetColumn is null)
            throw new ArgumentException("The target column does not belong to the target board.");
        await EnforceWipAsync(targetColumn, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var previousColumnId = item.BoardColumnId;
        var previousSprintId = item.SprintId;
        item.BoardId = targetBoard.Id;
        item.BoardColumnId = targetColumn.Id;
        item.SprintId = null;
        item.BoardRank = (await db.CoreWorkTasks
            .Where(x => x.BoardColumnId == targetColumn.Id)
            .MaxAsync(x => (long?)x.BoardRank, cancellationToken) ?? 0) + 1024;
        item.Status = StatusFor(targetColumn.Category);
        item.Revision++;
        item.UpdatedAt = now;
        AddActivity(
            organizationId, targetBoard.Id, item.Id, member, WorkItemActions.Transfer,
            "item.transferred", targetDecision,
            new
            {
                sourceBoardId,
                sourceColumnId = previousColumnId,
                sourceSprintId = previousSprintId,
                targetBoardId = targetBoard.Id,
                targetColumnId = targetColumn.Id,
                sourceGrantId = sourceDecision.GrantId
            },
            now,
            request.IdempotencyKey.Trim());
        await QueueRealtimeAsync(
            organizationId, sourceBoardId, item.Id, "item.transferred.out", item.Revision,
            cancellationToken, targetBoard.Id);
        await QueueRealtimeAsync(
            organizationId, targetBoard.Id, item.Id, "item.transferred.in", item.Revision,
            cancellationToken, sourceBoardId);
        await WorkSprintMetricsRecorder.RecordAsync(
            db, previousSprintId, "item.transferred.out",
            now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, targetBoard.Id, item.Id, member, WorkItemActions.Transfer,
            targetDecision,
            new { sourceBoardId, targetBoardId = targetBoard.Id, targetColumnId = targetColumn.Id },
            cancellationToken);
        return ToItem(item);
    }

    private async Task<OrganizationUser> ResolveMemberAsync(
        Guid organizationId,
        Guid applicationUserId,
        CancellationToken cancellationToken)
    {
        var member = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.ApplicationUserId == applicationUserId &&
            x.EmployeeType == EmployeeType.Human &&
            x.IsActive, cancellationToken)
            ?? throw new UnauthorizedAccessException("The current user is not an active organization member.");
        await WorkBoardProvisioning.EnsureLegacyGrantsAsync(
            db, organizationId, member, cancellationToken);
        return member;
    }

    private async Task<ScopedAuthorizationDecision> RequireAsync(
        Guid organizationId,
        Guid boardId,
        OrganizationUser member,
        string action,
        CancellationToken cancellationToken)
    {
        var decision = await authorization.AuthorizeAsync(
            organizationId, GrantSubjectKind.OrganizationUser, member.Id,
            action, GrantScopeKind.Board, boardId, cancellationToken);
        if (!decision.Allowed)
            throw new UnauthorizedAccessException(
                $"The current user does not have '{action}' on this board.");
        return decision;
    }

    private void AddActivity(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        OrganizationUser member,
        string action,
        string eventType,
        ScopedAuthorizationDecision decision,
        object data,
        DateTimeOffset occurredAt,
        string? idempotencyKey = null) =>
        db.WorkItemActivities.Add(new WorkItemActivity
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            BoardId = boardId,
            WorkItemId = itemId,
            EventType = eventType,
            Action = action,
            ActorKind = GrantSubjectKind.OrganizationUser,
            ActorSubjectId = member.Id,
            ActorDisplayName = member.DisplayName,
            AuthorizingGrantId = decision.GrantId,
            AuthorizingGrantRevision = decision.GrantRevision,
            IdempotencyKey = idempotencyKey,
            DataJson = JsonSerializer.Serialize(data, JsonOptions),
            OccurredAt = occurredAt
        });

    private async Task QueueRealtimeAsync(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        string changeType,
        long revision,
        CancellationToken cancellationToken,
        Guid? relatedBoardId = null)
    {
        var recipients = await ResolveRecipientsAsync(
            organizationId, boardId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        db.ApplicationRealtimeOutbox.Add(new ApplicationRealtimeOutboxItem
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            RecipientOrganizationUserIdsJson = JsonSerializer.Serialize(recipients, JsonOptions),
            EventType = AppRealtimeEvents.WorkBoardChanged,
            Subject = $"organizations/{organizationId:D}/work/boards/{boardId:D}",
            DataJson = JsonSerializer.Serialize(new
            {
                boardId,
                itemId,
                changeType,
                revision,
                relatedBoardId
            }, JsonOptions),
            Status = ApplicationRealtimeOutboxStatus.Pending,
            NextAttemptAt = now,
            OccurredAt = now
        });
    }

    private async Task<IReadOnlyList<Guid>> ResolveRecipientsAsync(
        Guid organizationId,
        Guid boardId,
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
            (x.Action == WorkBoardActions.Read || x.Action == WorkItemActions.Read))
            .Select(x => new { x.SubjectId, x.Action })
            .ToListAsync(cancellationToken);
        var boardReaders = grants.Where(x => x.Action == WorkBoardActions.Read)
            .Select(x => x.SubjectId).ToHashSet();
        var itemReaders = grants.Where(x => x.Action == WorkItemActions.Read)
            .Select(x => x.SubjectId).ToHashSet();
        boardReaders.IntersectWith(itemReaders);
        return await db.CoreOrganizationUsers.AsNoTracking().Where(x =>
                boardReaders.Contains(x.Id) &&
                x.OrganizationId == organizationId &&
                x.EmployeeType == EmployeeType.Human &&
                x.IsActive)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
    }

    private async Task EnforceWipAsync(
        WorkBoardColumn column,
        CancellationToken cancellationToken)
    {
        if (column.WipPolicy != WorkBoardWipPolicy.HardLimit || !column.WipLimit.HasValue)
            return;
        if (await db.CoreWorkTasks.CountAsync(
                x => x.BoardColumnId == column.Id, cancellationToken) >= column.WipLimit.Value)
            throw new InvalidOperationException(
                $"Column '{column.Name}' has reached its WIP limit of {column.WipLimit.Value}.");
    }

    private Task WriteAuditAsync(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        OrganizationUser member,
        string action,
        ScopedAuthorizationDecision decision,
        object data,
        CancellationToken cancellationToken) =>
        audit.AppendAsync(new AuditEventWriteRequest(
            action,
            "WorkManagement",
            "Inbound",
            "Completed",
            organizationId,
            "WorkItem",
            itemId,
            $"Completed {action}.",
            JsonSerializer.Serialize(new
            {
                boardId,
                grantId = decision.GrantId,
                grantRevision = decision.GrantRevision,
                data
            }, JsonOptions),
            Actor: new AuditActor(
                "Human", true, member.ApplicationUserId, member.Id, member.DisplayName)),
            cancellationToken);

    private static void ValidateMutation(
        string? value,
        string? idempotencyKey,
        string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{label} is required.");
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 160)
            throw new ArgumentException("A non-empty idempotency key of at most 160 characters is required.");
    }

    private static WorkItemCommentResponse ToComment(WorkItemComment comment) => new(
        comment.Id, comment.WorkItemId, comment.AuthorKind.ToString(),
        comment.AuthorSubjectId, comment.AuthorDisplayName, comment.Body,
        comment.Revision, comment.CreatedAt, comment.EditedAt);

    private static WorkBoardItemResponse ToItem(WorkTask item) => new(
        item.Id, item.BoardId!.Value, item.BoardColumnId!.Value,
        item.ParentWorkTaskId, item.SprintId, item.Kind.ToString(), item.Title, item.Description,
        item.Status.ToString(), item.Priority.ToString(),
        item.EstimatePoints, item.BoardRank, item.Revision,
        item.DueDate, item.CreatedAt, item.UpdatedAt);

    private static WorkTaskStatus StatusFor(WorkBoardColumnCategory category) => category switch
    {
        WorkBoardColumnCategory.ToDo => WorkTaskStatus.Ready,
        WorkBoardColumnCategory.InProgress => WorkTaskStatus.Running,
        WorkBoardColumnCategory.Done => WorkTaskStatus.Completed,
        WorkBoardColumnCategory.Cancelled => WorkTaskStatus.Cancelled,
        _ => WorkTaskStatus.Ready
    };
}
