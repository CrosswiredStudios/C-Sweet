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

public sealed class WorkSprintService(
    CSweetDbContext db,
    IScopedActionAuthorizationService authorization,
    IAuditEventWriter audit) : IWorkSprintService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<WorkSprintResponse>> ListAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default)
    {
        var member = await ResolveMemberAsync(
            organizationId, applicationUserId, cancellationToken);
        await RequireAsync(
            organizationId, boardId, member, WorkBoardActions.Read, cancellationToken);
        var decision = await RequireAsync(
            organizationId, boardId, member, WorkSprintActions.Read, cancellationToken);
        if (!await db.WorkBoards.AsNoTracking().AnyAsync(x =>
                x.Id == boardId && x.OrganizationId == organizationId,
                cancellationToken))
            throw new KeyNotFoundException("Board was not found.");
        var sprints = await db.WorkSprints.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.BoardId == boardId)
            .OrderByDescending(x => x.Status == WorkSprintStatus.Active)
            .ThenByDescending(x => x.StartsAt)
            .ThenByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        var counts = await db.CoreWorkTasks.AsNoTracking()
            .Where(x => x.BoardId == boardId && x.SprintId != null)
            .GroupBy(x => x.SprintId!.Value)
            .Select(x => new
            {
                SprintId = x.Key,
                Total = x.Count(),
                Completed = x.Count(item => item.Status == WorkTaskStatus.Completed),
                PlannedPoints = x.Sum(item => item.EstimatePoints ?? 0),
                CompletedPoints = x.Where(item => item.Status == WorkTaskStatus.Completed)
                    .Sum(item => item.EstimatePoints ?? 0)
            })
            .ToDictionaryAsync(x => x.SprintId, cancellationToken);
        await WriteAuditAsync(
            organizationId, boardId, boardId, "WorkBoard", member,
            WorkSprintActions.Read, decision, new { count = sprints.Count },
            cancellationToken);
        return sprints.Select(x =>
        {
            counts.TryGetValue(x.Id, out var count);
            return ToResponse(
                x, count?.Total ?? 0, count?.Completed ?? 0,
                count?.PlannedPoints ?? 0, count?.CompletedPoints ?? 0);
        }).ToList();
    }

    public async Task<WorkSprintResponse> CreateAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CreateWorkSprintRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = await ResolveMemberAsync(
            organizationId, applicationUserId, cancellationToken);
        var decision = await RequireAsync(
            organizationId, boardId, member, WorkSprintActions.Create, cancellationToken);
        ValidateIdempotencyKey(request.IdempotencyKey);
        var replay = await ReplayAsync<WorkSprintResponse>(
            member.Id, WorkSprintActions.Create, request.IdempotencyKey, cancellationToken);
        if (replay is not null) return replay;
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("Sprint name is required.");
        if (request.Name.Trim().Length > 160)
            throw new ArgumentException("Sprint name cannot exceed 160 characters.");
        if ((request.Goal?.Trim().Length ?? 0) > 2048)
            throw new ArgumentException("Sprint goal cannot exceed 2048 characters.");
        if (request.StartsAt.HasValue && request.EndsAt.HasValue &&
            request.EndsAt <= request.StartsAt)
            throw new ArgumentException("Sprint end must be after its start.");
        if (!await db.WorkBoards.AnyAsync(x =>
                x.Id == boardId &&
                x.OrganizationId == organizationId &&
                x.ArchivedAt == null, cancellationToken))
            throw new KeyNotFoundException("Board was not found.");

        var now = DateTimeOffset.UtcNow;
        var sprint = new WorkSprint
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            BoardId = boardId,
            Name = request.Name.Trim(),
            Goal = request.Goal?.Trim() ?? string.Empty,
            StartsAt = request.StartsAt,
            EndsAt = request.EndsAt,
            CreatedAt = now,
            UpdatedAt = now
        };
        var result = ToResponse(sprint, 0, 0, 0, 0);
        db.WorkSprints.Add(sprint);
        AddReceipt(
            organizationId, member.Id, WorkSprintActions.Create,
            request.IdempotencyKey, sprint.Id, result);
        await QueueRealtimeAsync(
            organizationId, boardId, "sprint.created",
            cancellationToken, sprint.Id);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, boardId, sprint.Id, "WorkSprint", member,
            WorkSprintActions.Create, decision,
            new { sprint.Id, sprint.Name, request.IdempotencyKey },
            cancellationToken);
        return result;
    }

    public async Task<WorkSprintResponse?> ChangeStateAsync(
        Guid organizationId,
        Guid boardId,
        Guid sprintId,
        Guid applicationUserId,
        string action,
        ChangeWorkSprintStateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (action is not (WorkSprintActions.Start or
            WorkSprintActions.Complete or WorkSprintActions.Cancel))
            throw new ArgumentException("The sprint state action is invalid.");
        var member = await ResolveMemberAsync(
            organizationId, applicationUserId, cancellationToken);
        var decision = await RequireAsync(
            organizationId, boardId, member, action, cancellationToken);
        ValidateIdempotencyKey(request.IdempotencyKey);
        var replay = await ReplayAsync<WorkSprintResponse>(
            member.Id, action, request.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (replay.Id != sprintId)
                throw new InvalidOperationException(
                    "The idempotency key was already used for a different sprint.");
            return replay;
        }
        var sprint = await db.WorkSprints.SingleOrDefaultAsync(x =>
            x.Id == sprintId &&
            x.OrganizationId == organizationId &&
            x.BoardId == boardId, cancellationToken);
        if (sprint is null) return null;
        if (sprint.Revision != request.ExpectedRevision)
            throw new DbUpdateConcurrencyException(
                $"Expected sprint revision {request.ExpectedRevision}, current revision is {sprint.Revision}.");

        var now = DateTimeOffset.UtcNow;
        switch (action)
        {
            case WorkSprintActions.Start:
                if (sprint.Status != WorkSprintStatus.Planned)
                    throw new InvalidOperationException("Only a planned sprint can be started.");
                if (await db.WorkSprints.AnyAsync(x =>
                        x.BoardId == boardId &&
                        x.Status == WorkSprintStatus.Active &&
                        x.Id != sprint.Id, cancellationToken))
                    throw new InvalidOperationException(
                        "This board already has an active sprint.");
                sprint.Status = WorkSprintStatus.Active;
                sprint.StartedAt = now;
                break;
            case WorkSprintActions.Complete:
                if (sprint.Status != WorkSprintStatus.Active)
                    throw new InvalidOperationException("Only an active sprint can be completed.");
                sprint.Status = WorkSprintStatus.Completed;
                sprint.CompletedAt = now;
                break;
            case WorkSprintActions.Cancel:
                if (sprint.Status is WorkSprintStatus.Completed or WorkSprintStatus.Cancelled)
                    throw new InvalidOperationException(
                        "A completed or cancelled sprint cannot be cancelled.");
                sprint.Status = WorkSprintStatus.Cancelled;
                sprint.CompletedAt = now;
                break;
        }
        sprint.Revision++;
        sprint.UpdatedAt = now;
        var counts = await SprintCountsAsync(sprint.Id, cancellationToken);
        if (action == WorkSprintActions.Complete)
            await WorkSprintSnapshotFactory.EnsureAsync(db, sprint, cancellationToken);
        await WorkSprintMetricsRecorder.RecordAsync(
            db, sprint.Id, EventTypeFor(action), now, cancellationToken);
        var result = ToResponse(
            sprint, counts.Total, counts.Completed,
            counts.PlannedPoints, counts.CompletedPoints);
        AddReceipt(
            organizationId, member.Id, action,
            request.IdempotencyKey, sprint.Id, result);
        await QueueRealtimeAsync(
            organizationId, boardId, EventTypeFor(action),
            cancellationToken, sprint.Id);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, boardId, sprint.Id, "WorkSprint", member,
            action, decision,
            new { sprint.Id, sprint.Status, sprint.Revision, request.IdempotencyKey },
            cancellationToken);
        return result;
    }

    public async Task<WorkBoardItemResponse?> SetItemSprintAsync(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        Guid applicationUserId,
        SetWorkItemSprintRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = await ResolveMemberAsync(
            organizationId, applicationUserId, cancellationToken);
        var decision = await RequireAsync(
            organizationId, boardId, member, WorkSprintActions.ManageScope,
            cancellationToken);
        ValidateIdempotencyKey(request.IdempotencyKey);
        var replay = await ReplayAsync<WorkBoardItemResponse>(
            member.Id, WorkSprintActions.ManageScope,
            request.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (replay.Id != itemId)
                throw new InvalidOperationException(
                    "The idempotency key was already used for a different work item.");
            return replay;
        }
        var item = await db.CoreWorkTasks.SingleOrDefaultAsync(x =>
            x.Id == itemId &&
            x.OrganizationId == organizationId &&
            x.BoardId == boardId, cancellationToken);
        if (item is null) return null;
        if (item.Revision != request.ExpectedItemRevision)
            throw new DbUpdateConcurrencyException(
                $"Expected work item revision {request.ExpectedItemRevision}, current revision is {item.Revision}.");
        if (request.SprintId.HasValue && !await db.WorkSprints.AnyAsync(x =>
                x.Id == request.SprintId.Value &&
                x.OrganizationId == organizationId &&
                x.BoardId == boardId &&
                (x.Status == WorkSprintStatus.Planned ||
                 x.Status == WorkSprintStatus.Active), cancellationToken))
            throw new ArgumentException(
                "The target sprint must be a planned or active sprint on this board.");

        var previousSprintId = item.SprintId;
        item.SprintId = request.SprintId;
        item.Revision++;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        var result = ToItem(item);
        db.WorkItemActivities.Add(new WorkItemActivity
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            BoardId = boardId,
            WorkItemId = item.Id,
            EventType = request.SprintId.HasValue
                ? "item.sprint.assigned"
                : "item.sprint.removed",
            Action = WorkSprintActions.ManageScope,
            ActorKind = GrantSubjectKind.OrganizationUser,
            ActorSubjectId = member.Id,
            ActorDisplayName = member.DisplayName,
            AuthorizingGrantId = decision.GrantId,
            AuthorizingGrantRevision = decision.GrantRevision,
            IdempotencyKey = request.IdempotencyKey,
            DataJson = JsonSerializer.Serialize(new
            {
                previousSprintId,
                sprintId = request.SprintId
            }, JsonOptions),
            OccurredAt = item.UpdatedAt
        });
        AddReceipt(
            organizationId, member.Id, WorkSprintActions.ManageScope,
            request.IdempotencyKey, item.Id, result);
        foreach (var sprintId in new[] { previousSprintId, request.SprintId }
                     .Where(x => x.HasValue).Distinct())
            await WorkSprintMetricsRecorder.RecordAsync(
                db, sprintId, request.SprintId.HasValue
                    ? "item.sprint.assigned"
                    : "item.sprint.removed",
                item.UpdatedAt, cancellationToken);
        await QueueRealtimeAsync(
            organizationId, boardId,
            request.SprintId.HasValue ? "item.sprint.assigned" : "item.sprint.removed",
            cancellationToken, request.SprintId, item.Id, item.Revision);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, boardId, item.Id, "WorkItem", member,
            WorkSprintActions.ManageScope, decision,
            new { previousSprintId, sprintId = request.SprintId, item.Revision },
            cancellationToken);
        return result;
    }

    public async Task<WorkBoardItemResponse?> SetItemEstimateAsync(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        Guid applicationUserId,
        SetWorkItemEstimateRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidatePoints(request.EstimatePoints, "Estimate");
        var member = await ResolveMemberAsync(
            organizationId, applicationUserId, cancellationToken);
        var decision = await RequireAsync(
            organizationId, boardId, member, WorkItemActions.Estimate,
            cancellationToken);
        ValidateIdempotencyKey(request.IdempotencyKey);
        var replay = await ReplayAsync<WorkBoardItemResponse>(
            member.Id, WorkItemActions.Estimate,
            request.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (replay.Id != itemId)
                throw new InvalidOperationException(
                    "The idempotency key was already used for a different work item.");
            return replay;
        }
        var item = await db.CoreWorkTasks.SingleOrDefaultAsync(x =>
            x.Id == itemId &&
            x.OrganizationId == organizationId &&
            x.BoardId == boardId, cancellationToken);
        if (item is null) return null;
        if (item.Revision != request.ExpectedItemRevision)
            throw new DbUpdateConcurrencyException(
                $"Expected work item revision {request.ExpectedItemRevision}, current revision is {item.Revision}.");
        var previousEstimate = item.EstimatePoints;
        item.EstimatePoints = request.EstimatePoints;
        item.Revision++;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        var result = ToItem(item);
        AddItemActivity(
            organizationId, boardId, item, member, WorkItemActions.Estimate,
            "item.estimate.changed", decision,
            new { previousEstimate, estimatePoints = request.EstimatePoints },
            request.IdempotencyKey);
        AddReceipt(
            organizationId, member.Id, WorkItemActions.Estimate,
            request.IdempotencyKey, item.Id, result);
        await WorkSprintMetricsRecorder.RecordAsync(
            db, item.SprintId, "item.estimate.changed",
            item.UpdatedAt, cancellationToken);
        await QueueRealtimeAsync(
            organizationId, boardId, "item.estimate.changed",
            cancellationToken, item.SprintId, item.Id, item.Revision);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, boardId, item.Id, "WorkItem", member,
            WorkItemActions.Estimate, decision,
            new { previousEstimate, estimatePoints = request.EstimatePoints, item.Revision },
            cancellationToken);
        return result;
    }

    public async Task<WorkSprintResponse?> SetCapacityAsync(
        Guid organizationId,
        Guid boardId,
        Guid sprintId,
        Guid applicationUserId,
        SetWorkSprintCapacityRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidatePoints(request.CapacityPoints, "Capacity");
        var member = await ResolveMemberAsync(
            organizationId, applicationUserId, cancellationToken);
        var decision = await RequireAsync(
            organizationId, boardId, member, WorkSprintActions.ManageCapacity,
            cancellationToken);
        ValidateIdempotencyKey(request.IdempotencyKey);
        var replay = await ReplayAsync<WorkSprintResponse>(
            member.Id, WorkSprintActions.ManageCapacity,
            request.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (replay.Id != sprintId)
                throw new InvalidOperationException(
                    "The idempotency key was already used for a different sprint.");
            return replay;
        }
        var sprint = await db.WorkSprints.SingleOrDefaultAsync(x =>
            x.Id == sprintId &&
            x.OrganizationId == organizationId &&
            x.BoardId == boardId, cancellationToken);
        if (sprint is null) return null;
        if (sprint.Status is WorkSprintStatus.Completed or WorkSprintStatus.Cancelled)
            throw new InvalidOperationException(
                "Capacity cannot be changed after a sprint is closed.");
        if (sprint.Revision != request.ExpectedSprintRevision)
            throw new DbUpdateConcurrencyException(
                $"Expected sprint revision {request.ExpectedSprintRevision}, current revision is {sprint.Revision}.");
        var previousCapacity = sprint.CapacityPoints;
        sprint.CapacityPoints = request.CapacityPoints;
        sprint.Revision++;
        sprint.UpdatedAt = DateTimeOffset.UtcNow;
        var counts = await SprintCountsAsync(sprint.Id, cancellationToken);
        var result = ToResponse(
            sprint, counts.Total, counts.Completed,
            counts.PlannedPoints, counts.CompletedPoints);
        AddReceipt(
            organizationId, member.Id, WorkSprintActions.ManageCapacity,
            request.IdempotencyKey, sprint.Id, result);
        await WorkSprintMetricsRecorder.RecordAsync(
            db, sprint.Id, "sprint.capacity.changed",
            sprint.UpdatedAt, cancellationToken);
        await QueueRealtimeAsync(
            organizationId, boardId, "sprint.capacity.changed",
            cancellationToken, sprint.Id, revision: sprint.Revision);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, boardId, sprint.Id, "WorkSprint", member,
            WorkSprintActions.ManageCapacity, decision,
            new { previousCapacity, capacityPoints = request.CapacityPoints, sprint.Revision },
            cancellationToken);
        return result;
    }

    public async Task<SprintCarryoverResponse?> CarryOverAsync(
        Guid organizationId,
        Guid boardId,
        Guid sourceSprintId,
        Guid applicationUserId,
        CarryOverSprintRequest request,
        CancellationToken cancellationToken = default)
    {
        if (sourceSprintId == request.TargetSprintId)
            throw new ArgumentException("Source and target sprint must be different.");
        var member = await ResolveMemberAsync(
            organizationId, applicationUserId, cancellationToken);
        var decision = await RequireAsync(
            organizationId, boardId, member, WorkSprintActions.CarryOver,
            cancellationToken);
        ValidateIdempotencyKey(request.IdempotencyKey);
        var replay = await ReplayAsync<SprintCarryoverResponse>(
            member.Id, WorkSprintActions.CarryOver,
            request.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (replay.SourceSprintId != sourceSprintId)
                throw new InvalidOperationException(
                    "The idempotency key was already used for a different carryover.");
            return replay;
        }
        var source = await db.WorkSprints.SingleOrDefaultAsync(x =>
            x.Id == sourceSprintId &&
            x.OrganizationId == organizationId &&
            x.BoardId == boardId, cancellationToken);
        if (source is null) return null;
        if (source.Status is not (WorkSprintStatus.Completed or WorkSprintStatus.Cancelled))
            throw new InvalidOperationException(
                "Only a completed or cancelled sprint can be carried over.");
        if (source.Revision != request.ExpectedSourceSprintRevision)
            throw new DbUpdateConcurrencyException(
                $"Expected sprint revision {request.ExpectedSourceSprintRevision}, current revision is {source.Revision}.");
        var target = await db.WorkSprints.SingleOrDefaultAsync(x =>
            x.Id == request.TargetSprintId &&
            x.OrganizationId == organizationId &&
            x.BoardId == boardId &&
            (x.Status == WorkSprintStatus.Planned ||
             x.Status == WorkSprintStatus.Active), cancellationToken)
            ?? throw new ArgumentException(
                "The target must be a planned or active sprint on this board.");
        var requestedIds = request.ItemIds?.Distinct().ToHashSet();
        if (requestedIds?.Count > 500)
            throw new ArgumentException("At most 500 items can be carried over at once.");
        var candidates = await db.CoreWorkTasks.Where(x =>
                x.BoardId == boardId &&
                x.SprintId == sourceSprintId &&
                x.Status != WorkTaskStatus.Completed)
            .ToListAsync(cancellationToken);
        if (requestedIds is not null)
        {
            var availableIds = candidates.Select(x => x.Id).ToHashSet();
            if (!requestedIds.IsSubsetOf(availableIds))
                throw new ArgumentException(
                    "Every requested item must be incomplete and belong to the source sprint.");
            candidates = candidates.Where(x => requestedIds.Contains(x.Id)).ToList();
        }
        var now = DateTimeOffset.UtcNow;
        foreach (var item in candidates)
        {
            item.SprintId = target.Id;
            item.Revision++;
            item.UpdatedAt = now;
            AddItemActivity(
                organizationId, boardId, item, member, WorkSprintActions.CarryOver,
                "item.sprint.carried-over", decision,
                new { sourceSprintId, targetSprintId = target.Id });
        }
        source.Revision++;
        source.UpdatedAt = now;
        target.Revision++;
        target.UpdatedAt = now;
        var result = new SprintCarryoverResponse(
            source.Id, target.Id, candidates.Select(x => x.Id).ToList(),
            candidates.Sum(x => x.EstimatePoints ?? 0));
        AddReceipt(
            organizationId, member.Id, WorkSprintActions.CarryOver,
            request.IdempotencyKey, source.Id, result);
        await WorkSprintMetricsRecorder.RecordAsync(
            db, source.Id, "sprint.items.carried-over",
            now, cancellationToken);
        await WorkSprintMetricsRecorder.RecordAsync(
            db, target.Id, "sprint.items.carried-over",
            now, cancellationToken);
        await QueueRealtimeAsync(
            organizationId, boardId, "sprint.items.carried-over",
            cancellationToken, target.Id, revision: target.Revision);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, boardId, source.Id, "WorkSprint", member,
            WorkSprintActions.CarryOver, decision,
            new
            {
                targetSprintId = target.Id,
                itemIds = result.ItemIds,
                result.CarriedPoints
            },
            cancellationToken);
        return result;
    }

    public async Task<WorkSprintReportResponse> GetReportAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default)
    {
        var member = await ResolveMemberAsync(
            organizationId, applicationUserId, cancellationToken);
        await RequireAsync(
            organizationId, boardId, member, WorkBoardActions.Read,
            cancellationToken);
        var decision = await RequireAsync(
            organizationId, boardId, member, WorkSprintActions.ReadReports,
            cancellationToken);
        if (!await db.WorkBoards.AsNoTracking().AnyAsync(x =>
                x.Id == boardId && x.OrganizationId == organizationId,
                cancellationToken))
            throw new KeyNotFoundException("Board was not found.");
        var result = await WorkSprintReportBuilder.BuildAsync(
            db, organizationId, boardId, cancellationToken);
        await WriteAuditAsync(
            organizationId, boardId, boardId, "WorkBoard", member,
            WorkSprintActions.ReadReports, decision,
            new { result.CompletedSprintCount }, cancellationToken);
        return result;
    }

    private void AddItemActivity(
        Guid organizationId,
        Guid boardId,
        WorkTask item,
        OrganizationUser member,
        string action,
        string eventType,
        ScopedAuthorizationDecision decision,
        object data,
        string? idempotencyKey = null) =>
        db.WorkItemActivities.Add(new WorkItemActivity
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            BoardId = boardId,
            WorkItemId = item.Id,
            EventType = eventType,
            Action = action,
            ActorKind = GrantSubjectKind.OrganizationUser,
            ActorSubjectId = member.Id,
            ActorDisplayName = member.DisplayName,
            AuthorizingGrantId = decision.GrantId,
            AuthorizingGrantRevision = decision.GrantRevision,
            IdempotencyKey = idempotencyKey,
            DataJson = JsonSerializer.Serialize(data, JsonOptions),
            OccurredAt = item.UpdatedAt
        });

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
            ?? throw new UnauthorizedAccessException(
                "The current user is not an active organization member.");
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

    private async Task<T?> ReplayAsync<T>(
        Guid actorSubjectId,
        string action,
        string idempotencyKey,
        CancellationToken cancellationToken) where T : class
    {
        var json = await db.WorkSprintMutationReceipts.AsNoTracking()
            .Where(x =>
                x.ActorKind == GrantSubjectKind.OrganizationUser &&
                x.ActorSubjectId == actorSubjectId &&
                x.Action == action &&
                x.IdempotencyKey == idempotencyKey)
            .Select(x => x.ResultJson)
            .SingleOrDefaultAsync(cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private void AddReceipt<T>(
        Guid organizationId,
        Guid actorSubjectId,
        string action,
        string idempotencyKey,
        Guid resourceId,
        T result) =>
        db.WorkSprintMutationReceipts.Add(new WorkSprintMutationReceipt
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ActorKind = GrantSubjectKind.OrganizationUser,
            ActorSubjectId = actorSubjectId,
            Action = action,
            IdempotencyKey = idempotencyKey,
            ResourceId = resourceId,
            ResultJson = JsonSerializer.Serialize(result, JsonOptions),
            CreatedAt = DateTimeOffset.UtcNow
        });

    private async Task<(
        int Total,
        int Completed,
        decimal PlannedPoints,
        decimal CompletedPoints)> SprintCountsAsync(
        Guid sprintId,
        CancellationToken cancellationToken)
    {
        var total = await db.CoreWorkTasks.CountAsync(
            x => x.SprintId == sprintId, cancellationToken);
        var completed = await db.CoreWorkTasks.CountAsync(
            x => x.SprintId == sprintId &&
                 x.Status == WorkTaskStatus.Completed, cancellationToken);
        var plannedPoints = await db.CoreWorkTasks.Where(x => x.SprintId == sprintId)
            .SumAsync(x => x.EstimatePoints ?? 0, cancellationToken);
        var completedPoints = await db.CoreWorkTasks.Where(x =>
                x.SprintId == sprintId && x.Status == WorkTaskStatus.Completed)
            .SumAsync(x => x.EstimatePoints ?? 0, cancellationToken);
        return (total, completed, plannedPoints, completedPoints);
    }

    private async Task QueueRealtimeAsync(
        Guid organizationId,
        Guid boardId,
        string changeType,
        CancellationToken cancellationToken,
        Guid? sprintId = null,
        Guid? itemId = null,
        long? revision = null)
    {
        var recipients = await ResolveRecipientsAsync(
            organizationId, boardId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
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
                sprintId,
                itemId,
                changeType,
                revision
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
                (x.Action == WorkBoardActions.Read ||
                 x.Action == WorkItemActions.Read ||
                 x.Action == WorkSprintActions.Read))
            .Select(x => new { x.SubjectId, x.Action })
            .ToListAsync(cancellationToken);
        var boardReaders = grants.Where(x => x.Action == WorkBoardActions.Read)
            .Select(x => x.SubjectId).ToHashSet();
        var detailReaders = grants.Where(x =>
                x.Action == WorkItemActions.Read ||
                x.Action == WorkSprintActions.Read)
            .Select(x => x.SubjectId).ToHashSet();
        boardReaders.IntersectWith(detailReaders);
        return await db.CoreOrganizationUsers.AsNoTracking().Where(x =>
                boardReaders.Contains(x.Id) &&
                x.OrganizationId == organizationId &&
                x.EmployeeType == EmployeeType.Human &&
                x.IsActive)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
    }

    private Task WriteAuditAsync(
        Guid organizationId,
        Guid boardId,
        Guid entityId,
        string entityType,
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
            entityType,
            entityId,
            $"Completed {action}.",
            JsonSerializer.Serialize(new
            {
                boardId,
                grantId = decision.GrantId,
                grantRevision = decision.GrantRevision,
                data
            }, JsonOptions),
            Actor: new AuditActor(
                "Human", true, member.ApplicationUserId, member.Id,
                member.DisplayName)),
            cancellationToken);

    private static void ValidateIdempotencyKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160)
            throw new ArgumentException(
                "A non-empty idempotency key of at most 160 characters is required.");
    }

    private static void ValidatePoints(decimal? points, string label)
    {
        if (points is < 0 or > 999999.99m)
            throw new ArgumentException(
                $"{label} points must be between 0 and 999999.99.");
    }

    private static string EventTypeFor(string action) => action switch
    {
        WorkSprintActions.Start => "sprint.started",
        WorkSprintActions.Complete => "sprint.completed",
        WorkSprintActions.Cancel => "sprint.cancelled",
        _ => "sprint.changed"
    };

    private static WorkSprintResponse ToResponse(
        WorkSprint sprint,
        int itemCount,
        int completedItemCount,
        decimal plannedPoints,
        decimal completedPoints) => new(
        sprint.Id, sprint.BoardId, sprint.Name, sprint.Goal, sprint.Status.ToString(),
        sprint.StartsAt, sprint.EndsAt, sprint.StartedAt, sprint.CompletedAt,
        sprint.CapacityPoints, itemCount, completedItemCount,
        plannedPoints, completedPoints, sprint.Revision,
        sprint.CreatedAt, sprint.UpdatedAt);

    private static WorkBoardItemResponse ToItem(WorkTask item) => new(
        item.Id, item.BoardId!.Value, item.BoardColumnId!.Value,
        item.ParentWorkTaskId, item.SprintId, item.Kind.ToString(),
        item.Title, item.Description, item.Status.ToString(), item.Priority.ToString(),
        item.EstimatePoints, item.BoardRank, item.Revision,
        item.DueDate, item.CreatedAt, item.UpdatedAt);
}
