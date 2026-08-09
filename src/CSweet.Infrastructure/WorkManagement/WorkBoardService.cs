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
using System.Text.RegularExpressions;

namespace CSweet.Infrastructure.WorkManagement;

public sealed class WorkBoardService(
    CSweetDbContext db,
    IScopedActionAuthorizationService authorization,
    IAuditEventWriter audit) : IWorkBoardService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<WorkBoardDirectoryResponse> ListDirectoryAsync(
        Guid organizationId,
        Guid applicationUserId,
        WorkBoardDirectoryQuery query,
        CancellationToken cancellationToken = default)
    {
        var member = await ResolveMemberAsync(organizationId, applicationUserId, cancellationToken);
        await WorkBoardProvisioning.EnsureLegacyGrantsAsync(db, organizationId, member, cancellationToken);
        var readGrants = await ActiveGrantsAsync(organizationId, member.Id, cancellationToken);
        if (!readGrants.Any(x => x.Action == WorkBoardActions.Read))
        {
            await WriteDeniedAsync(organizationId, member, WorkBoardActions.Read, null, cancellationToken);
            throw new UnauthorizedAccessException("The current user does not have a board read grant.");
        }

        await WorkBoardProvisioning.EnsureDefaultBoardAsync(db, organizationId, cancellationToken);

        var accessibleBoardIds = readGrants
            .Where(x => x.Action == WorkBoardActions.Read && x.ScopeKind == GrantScopeKind.Board && x.ScopeId.HasValue)
            .Select(x => x.ScopeId!.Value)
            .ToHashSet();
        var organizationRead = readGrants.Any(x =>
            x.Action == WorkBoardActions.Read && x.ScopeKind == GrantScopeKind.Organization);

        var boards = db.WorkBoards.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && !x.IsPersonalTodo)
            .Where(x => organizationRead || accessibleBoardIds.Contains(x.Id));
        if (!query.IncludeArchived)
            boards = boards.Where(x => x.ArchivedAt == null);
        if (query.WorkstreamId.HasValue)
            boards = boards.Where(x => x.WorkstreamId == query.WorkstreamId);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim().ToLower();
            boards = boards.Where(x =>
                x.Name.ToLower().Contains(search) || x.Description.ToLower().Contains(search));
        }

        var boardRows = await boards
            .Select(x => new
            {
                Board = x,
                ActiveItemCount = db.CoreWorkTasks.Count(task => task.BoardId == x.Id &&
                    task.Status != WorkTaskStatus.Completed && task.Status != WorkTaskStatus.Cancelled),
                Preference = db.WorkBoardUserPreferences
                    .Where(preference => preference.BoardId == x.Id &&
                        preference.OrganizationUserId == member.Id)
                    .Select(preference => new { preference.IsFavorite, preference.LastVisitedAt })
                    .SingleOrDefault()
            })
            .ToListAsync(cancellationToken);

        if (query.FavoritesOnly)
            boardRows = boardRows.Where(x => x.Preference?.IsFavorite == true).ToList();

        var allOrganizationGrants = await ActiveOrganizationGrantsAsync(organizationId, cancellationToken);
        var summaries = boardRows
            .Select(x => ToSummary(
                x.Board,
                member.Id,
                readGrants,
                allOrganizationGrants,
                x.Preference?.IsFavorite ?? false,
                x.Preference?.LastVisitedAt,
                x.ActiveItemCount))
            .OrderByDescending(x => x.IsFavorite)
            .ThenByDescending(x => x.LastVisitedAt)
            .ThenByDescending(x => x.IsDefault)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var create = await authorization.AuthorizeAsync(
            organizationId, GrantSubjectKind.OrganizationUser, member.Id,
            WorkBoardActions.Create, GrantScopeKind.Organization, null, cancellationToken);
        if (!create.Allowed)
        {
            create = readGrants.Any(x =>
                x.Action == WorkBoardActions.Create &&
                x.ScopeKind == GrantScopeKind.Team &&
                x.ScopeId.HasValue)
                ? new ScopedAuthorizationDecision(true, WorkBoardActions.Create)
                : create;
        }
        await WriteAllowedAsync(
            organizationId, member, WorkBoardActions.Read, null,
            readGrants.First(x => x.Action == WorkBoardActions.Read).Id,
            new { count = summaries.Count, query.Search, query.WorkstreamId, query.IncludeArchived, query.FavoritesOnly },
            cancellationToken);
        return new WorkBoardDirectoryResponse(summaries, create.Allowed);
    }

    public async Task<WorkBoardDetailResponse?> GetAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default)
    {
        var member = await ResolveMemberAsync(organizationId, applicationUserId, cancellationToken);
        var decision = await RequireAsync(
            organizationId, member, WorkBoardActions.Read, boardId, cancellationToken);
        var board = await db.WorkBoards
            .Include(x => x.Columns.OrderBy(column => column.Position))
            .SingleOrDefaultAsync(x =>
                x.Id == boardId && x.OrganizationId == organizationId && !x.IsPersonalTodo,
                cancellationToken);
        if (board is null) return null;
        await WorkBoardProvisioning.EnsureTaskPlacementAsync(db, board, cancellationToken);
        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(cancellationToken);

        var preference = await db.WorkBoardUserPreferences.SingleOrDefaultAsync(x =>
            x.BoardId == boardId && x.OrganizationUserId == member.Id, cancellationToken);
        if (preference is null)
        {
            preference = new WorkBoardUserPreference
            {
                Id = Guid.NewGuid(),
                BoardId = boardId,
                OrganizationUserId = member.Id
            };
            db.WorkBoardUserPreferences.Add(preference);
        }
        preference.LastVisitedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var detail = await ToDetailAsync(board, member.Id, cancellationToken);
        await WriteAllowedAsync(
            organizationId, member, WorkBoardActions.Read, boardId,
            decision.GrantId!.Value, new { boardId }, cancellationToken);
        return detail;
    }

    public async Task<WorkBoardDetailResponse> CreateAsync(
        Guid organizationId,
        Guid applicationUserId,
        CreateWorkBoardRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = await ResolveMemberAsync(organizationId, applicationUserId, cancellationToken);
        var decision = await RequireCreateAsync(
            organizationId, member, request.TeamId, cancellationToken);
        await ValidateRequestAsync(
            organizationId, request.Name, request.WorkstreamId, request.TeamId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var managerId = request.ManagerOrganizationUserId ??
            (request.TeamId.HasValue
                ? await db.OrganizationTeams.Where(x => x.Id == request.TeamId.Value)
                    .Select(x => x.LeadOrganizationUserId).SingleAsync(cancellationToken)
                : member.Id);
        await ValidateManagerAsync(organizationId, managerId, cancellationToken);
        var boardKey = await ResolveBoardKeyAsync(
            organizationId, request.Key, request.Name, null, cancellationToken);
        var board = new WorkBoard
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            WorkstreamId = request.WorkstreamId,
            TeamId = request.TeamId,
            ManagerOrganizationUserId = managerId,
            Key = boardKey,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            CreatedAt = now,
            UpdatedAt = now,
            Columns =
            [
                NewColumn("To Do", WorkBoardColumnCategory.ToDo, 0),
                NewColumn("Done", WorkBoardColumnCategory.Done, 1)
            ]
        };
        db.WorkBoards.Add(board);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAllowedAsync(
            organizationId, member, WorkBoardActions.Create, board.Id,
            decision.GrantId!.Value,
            new { board.Id, board.Name, board.WorkstreamId, board.TeamId },
            cancellationToken);
        return await ToDetailAsync(board, member.Id, cancellationToken);
    }

    public async Task<WorkBoardDetailResponse?> UpdateAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        UpdateWorkBoardRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = await ResolveMemberAsync(organizationId, applicationUserId, cancellationToken);
        var decision = await RequireAsync(
            organizationId, member, WorkBoardActions.Configure, boardId, cancellationToken);
        await ValidateRequestAsync(
            organizationId, request.Name, request.WorkstreamId, teamId: null, cancellationToken);
        var board = await db.WorkBoards
            .Include(x => x.Columns.OrderBy(column => column.Position))
            .SingleOrDefaultAsync(x =>
                x.Id == boardId && x.OrganizationId == organizationId && !x.IsPersonalTodo,
                cancellationToken);
        if (board is null) return null;
        if (board.ArchivedAt.HasValue)
            throw new InvalidOperationException("Archived boards must be restored before they can be configured.");
        if (request.ExpectedRevision.HasValue && request.ExpectedRevision != board.Revision)
            throw new DbUpdateConcurrencyException("The board changed since it was loaded.");

        if (request.IsDefault && !board.IsDefault)
        {
            var defaults = await db.WorkBoards
                .Where(x => x.OrganizationId == organizationId && x.IsDefault && x.Id != boardId)
                .ToListAsync(cancellationToken);
            foreach (var current in defaults)
                current.IsDefault = false;
        }
        board.Name = request.Name.Trim();
        board.Description = request.Description?.Trim() ?? string.Empty;
        board.WorkstreamId = request.WorkstreamId;
        if (request.ManagerOrganizationUserId.HasValue)
        {
            await ValidateManagerAsync(
                organizationId, request.ManagerOrganizationUserId.Value, cancellationToken);
            board.ManagerOrganizationUserId = request.ManagerOrganizationUserId;
        }
        if (!string.IsNullOrWhiteSpace(request.Key))
            board.Key = await ResolveBoardKeyAsync(
                organizationId, request.Key, request.Name, board.Id, cancellationToken);
        board.IsDefault = request.IsDefault || board.IsDefault;
        board.Revision++;
        board.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await WriteAllowedAsync(
            organizationId, member, WorkBoardActions.Configure, board.Id,
            decision.GrantId!.Value,
            new { board.Id, board.Name, board.WorkstreamId, board.IsDefault }, cancellationToken);
        return await ToDetailAsync(board, member.Id, cancellationToken);
    }

    public Task<bool> ArchiveAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default) =>
        SetArchiveStateAsync(organizationId, boardId, applicationUserId, archive: true, cancellationToken);

    public Task<bool> RestoreAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default) =>
        SetArchiveStateAsync(organizationId, boardId, applicationUserId, archive: false, cancellationToken);

    public async Task<bool> SetFavoriteAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        bool isFavorite,
        CancellationToken cancellationToken = default)
    {
        var member = await ResolveMemberAsync(organizationId, applicationUserId, cancellationToken);
        await RequireAsync(organizationId, member, WorkBoardActions.Read, boardId, cancellationToken);
        if (!await db.WorkBoards.AnyAsync(x =>
                x.Id == boardId && x.OrganizationId == organizationId && !x.IsPersonalTodo,
                cancellationToken))
            return false;
        var preference = await db.WorkBoardUserPreferences.SingleOrDefaultAsync(x =>
            x.BoardId == boardId && x.OrganizationUserId == member.Id, cancellationToken);
        if (preference is null)
        {
            preference = new WorkBoardUserPreference
            {
                Id = Guid.NewGuid(),
                BoardId = boardId,
                OrganizationUserId = member.Id
            };
            db.WorkBoardUserPreferences.Add(preference);
        }
        preference.IsFavorite = isFavorite;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<WorkBoardDetailResponse?> ConfigureColumnsAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        ConfigureWorkBoardColumnsRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = await ResolveMemberAsync(organizationId, applicationUserId, cancellationToken);
        var decision = await RequireAsync(
            organizationId, member, WorkBoardActions.ConfigureColumns, boardId, cancellationToken);
        var board = await db.WorkBoards
            .Include(x => x.Columns)
            .SingleOrDefaultAsync(x =>
                x.Id == boardId && x.OrganizationId == organizationId && !x.IsPersonalTodo,
                cancellationToken);
        if (board is null) return null;
        if (board.ArchivedAt.HasValue)
            throw new InvalidOperationException("Archived boards must be restored before their columns can be configured.");
        if (board.Revision != request.ExpectedRevision)
            throw new DbUpdateConcurrencyException("The board changed since it was loaded.");
        if (request.Columns.Count == 0)
            throw new ArgumentException("At least one board column is required.");

        var parsed = request.Columns.Select((column, position) => new
        {
            column.Id,
            Name = column.Name.Trim(),
            Category = ParseEnum<WorkBoardColumnCategory>(column.Category, "column category"),
            WipPolicy = ParseEnum<WorkBoardWipPolicy>(column.WipPolicy, "WIP policy"),
            column.WipLimit,
            Position = position
        }).ToList();
        if (parsed.Any(x => string.IsNullOrWhiteSpace(x.Name)))
            throw new ArgumentException("Every board column requires a name.");
        if (parsed.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != parsed.Count)
            throw new ArgumentException("Board column names must be unique.");
        if (!parsed.Any(x => x.Category == WorkBoardColumnCategory.ToDo) ||
            !parsed.Any(x => x.Category == WorkBoardColumnCategory.Done))
            throw new ArgumentException("A board requires at least one To Do column and one Done column.");
        if (parsed.Any(x => x.WipPolicy != WorkBoardWipPolicy.Disabled &&
                            (!x.WipLimit.HasValue || x.WipLimit <= 0)))
            throw new ArgumentException("Warning and hard WIP policies require a positive limit.");

        var requestedIds = parsed.Where(x => x.Id.HasValue).Select(x => x.Id!.Value).ToHashSet();
        if (requestedIds.Count != parsed.Count(x => x.Id.HasValue) ||
            requestedIds.Any(id => board.Columns.All(x => x.Id != id)))
            throw new ArgumentException("A column identifier is duplicated or does not belong to this board.");

        var removed = board.Columns.Where(x => !requestedIds.Contains(x.Id)).ToList();
        var occupiedRemovedIds = await db.CoreWorkTasks
            .Where(x => x.BoardId == boardId && x.BoardColumnId != null &&
                        removed.Select(column => column.Id).Contains(x.BoardColumnId.Value))
            .Select(x => x.BoardColumnId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (occupiedRemovedIds.Count > 0)
            throw new InvalidOperationException("Move all cards out of a column before removing it.");

        db.WorkBoardColumns.RemoveRange(removed);
        foreach (var input in parsed)
        {
            var column = input.Id.HasValue
                ? board.Columns.Single(x => x.Id == input.Id.Value)
                : new WorkBoardColumn { Id = Guid.NewGuid(), BoardId = board.Id };
            column.Name = input.Name;
            column.Category = input.Category;
            column.Position = input.Position;
            column.WipPolicy = input.WipPolicy;
            column.WipLimit = input.WipPolicy == WorkBoardWipPolicy.Disabled ? null : input.WipLimit;
            if (!input.Id.HasValue) db.WorkBoardColumns.Add(column);
        }

        board.Revision++;
        board.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await WriteAllowedAsync(
            organizationId, member, WorkBoardActions.ConfigureColumns, board.Id,
            decision.GrantId!.Value,
            new { board.Id, board.Revision, columnCount = parsed.Count }, cancellationToken);
        return await GetDetailWithoutVisitAsync(board, member.Id, cancellationToken);
    }

    public async Task<WorkBoardItemResponse> CreateItemAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CreateBoardWorkItemRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = await ResolveMemberAsync(organizationId, applicationUserId, cancellationToken);
        var decision = await RequireAsync(
            organizationId, member, WorkItemActions.Create, boardId, cancellationToken);
        var board = await db.WorkBoards
            .Include(x => x.Columns)
            .SingleOrDefaultAsync(x =>
                x.Id == boardId && x.OrganizationId == organizationId &&
                x.ArchivedAt == null && !x.IsPersonalTodo,
                cancellationToken)
            ?? throw new KeyNotFoundException("Board was not found.");
        if (string.IsNullOrWhiteSpace(request.Title))
            throw new ArgumentException("Work item title is required.");
        var kind = ParseEnum<WorkItemKind>(request.Kind, "work item kind");
        var priority = ParseEnum<WorkTaskPriority>(request.Priority, "work item priority");
        var column = request.ColumnId.HasValue
            ? board.Columns.SingleOrDefault(x => x.Id == request.ColumnId.Value)
            : board.Columns.OrderBy(x => x.Position)
                .FirstOrDefault(x => x.Category == WorkBoardColumnCategory.ToDo);
        if (column is null)
            throw new ArgumentException("The selected column does not belong to this board.");
        await EnforceWipLimitAsync(boardId, column, null, cancellationToken);

        if (request.ParentItemId.HasValue && !await db.CoreWorkTasks.AnyAsync(x =>
                x.Id == request.ParentItemId &&
                x.OrganizationId == organizationId &&
                x.BoardId == boardId, cancellationToken))
            throw new ArgumentException("The parent work item must belong to the same board.");

        var executable = kind is not (WorkItemKind.Initiative or WorkItemKind.Epic);
        if (executable && !request.AccountableOrganizationUserId.HasValue)
            throw new ArgumentException("Executable work items require an accountable organization user.");
        if (request.AccountableOrganizationUserId.HasValue)
            await ValidateManagerAsync(
                organizationId, request.AccountableOrganizationUserId.Value, cancellationToken);
        var published = await db.WorkOrchestrationPolicies.AsNoTracking()
            .Where(x => x.BoardId == boardId && x.PublishedRevisionId != null)
            .Select(x => x.PublishedRevisionId)
            .SingleOrDefaultAsync(cancellationToken);
        if (executable && !published.HasValue)
            throw new InvalidOperationException(
                "Publish an orchestration policy before creating executable work items.");
        var policyStages = published.HasValue
            ? await db.WorkOrchestrationStages.AsNoTracking()
                .Where(x => x.PolicyRevisionId == published.Value)
                .ToListAsync(cancellationToken)
            : [];
        ValidateStageAssignments(executable, policyStages, request.StageAssignments);

        var now = DateTimeOffset.UtcNow;
        var item = new WorkTask
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            BoardId = boardId,
            BoardColumnId = column.Id,
            AccountableOrganizationUserId = request.AccountableOrganizationUserId,
            IdentifierSequence = board.NextItemSequence,
            Identifier = $"{board.Key}-{board.NextItemSequence}",
            ParentWorkTaskId = request.ParentItemId,
            Kind = kind,
            Title = request.Title.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            Status = StatusFor(column.Category),
            Priority = priority,
            BoardRank = (await db.CoreWorkTasks
                .Where(x => x.BoardColumnId == column.Id)
                .MaxAsync(x => (long?)x.BoardRank, cancellationToken) ?? 0) + 1024,
            DueDate = request.DueDate,
            CreatedAt = now,
            UpdatedAt = now
        };
        board.NextItemSequence++;
        db.CoreWorkTasks.Add(item);
        foreach (var assignment in request.StageAssignments)
        {
            db.WorkItemStageAssignments.Add(new WorkItemStageAssignment
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                BoardId = boardId,
                WorkItemId = item.Id,
                StageKey = assignment.StageKey,
                PrincipalKind = ParseEnum<WorkOrchestrationPrincipalKind>(
                    assignment.PrincipalKind, "stage assignment principal kind"),
                OrganizationUserId = assignment.OrganizationUserId,
                AgentInstallationId = assignment.AgentInstallationId,
                PlatformAction = assignment.PlatformAction,
                CreatedAt = now
            });
        }
        AddActivity(
            organizationId, boardId, item.Id, member, WorkItemActions.Create,
            "item.created", decision, new { columnId = column.Id }, now);
        await QueueRealtimeAsync(
            organizationId, boardId, item.Id, "item.created",
            item.Revision, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAllowedAsync(
            organizationId, member, WorkItemActions.Create, boardId,
            decision.GrantId!.Value,
            new { boardId, item.Id, item.Kind, item.BoardColumnId }, cancellationToken);
        return ToItemResponse(item);
    }

    public async Task<WorkBoardItemResponse?> MoveItemAsync(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        Guid applicationUserId,
        MoveBoardWorkItemRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = await ResolveMemberAsync(organizationId, applicationUserId, cancellationToken);
        if (!await db.WorkBoards.AsNoTracking().AnyAsync(x =>
                x.Id == boardId && x.OrganizationId == organizationId && !x.IsPersonalTodo,
                cancellationToken))
            return null;
        var item = await db.CoreWorkTasks.SingleOrDefaultAsync(x =>
            x.Id == itemId && x.OrganizationId == organizationId && x.BoardId == boardId,
            cancellationToken);
        if (item is null) return null;
        if (item.Revision != request.ExpectedRevision)
            throw new DbUpdateConcurrencyException("The work item changed since it was loaded.");
        if (await db.WorkItemExecutions.AnyAsync(x =>
                x.WorkItemId == itemId &&
                (x.SprintExecution!.Status == WorkSprintExecutionStatus.Active ||
                 x.SprintExecution.Status == WorkSprintExecutionStatus.Paused),
                cancellationToken))
            throw new InvalidOperationException(
                "Automated sprint cards are transitioned only by the work orchestrator.");

        var target = await db.WorkBoardColumns.SingleOrDefaultAsync(x =>
            x.Id == request.TargetColumnId && x.BoardId == boardId, cancellationToken)
            ?? throw new ArgumentException("The target column does not belong to this board.");
        var action = target.Category switch
        {
            WorkBoardColumnCategory.Done when item.Status != WorkTaskStatus.Completed =>
                WorkItemActions.Complete,
            WorkBoardColumnCategory.Cancelled when item.Status != WorkTaskStatus.Cancelled =>
                WorkItemActions.Cancel,
            WorkBoardColumnCategory.ToDo or WorkBoardColumnCategory.InProgress
                when item.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled =>
                WorkItemActions.Reopen,
            _ => WorkItemActions.Move
        };
        var decision = await RequireAsync(
            organizationId, member, action, boardId, cancellationToken);
        await EnforceWipLimitAsync(boardId, target, item.Id, cancellationToken);

        var targetItems = await db.CoreWorkTasks
            .Where(x => x.BoardColumnId == target.Id && x.Id != item.Id)
            .OrderBy(x => x.BoardRank)
            .ToListAsync(cancellationToken);
        var sourceColumnId = item.BoardColumnId;
        item.BoardRank = RankBefore(targetItems, request.BeforeItemId);
        item.BoardColumnId = target.Id;
        item.Status = StatusFor(target.Category);
        item.Revision++;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        AddActivity(
            organizationId, boardId, item.Id, member, action,
            EventTypeFor(action), decision,
            new { sourceColumnId, targetColumnId = target.Id, item.BoardRank },
            item.UpdatedAt);
        await QueueRealtimeAsync(
            organizationId, boardId, item.Id, EventTypeFor(action),
            item.Revision, cancellationToken);
        await WorkSprintMetricsRecorder.RecordAsync(
            db, item.SprintId, EventTypeFor(action),
            item.UpdatedAt, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAllowedAsync(
            organizationId, member, action, boardId, decision.GrantId!.Value,
            new { boardId, item.Id, targetColumnId = target.Id, item.BoardRank, item.Revision },
            cancellationToken);
        return ToItemResponse(item);
    }

    private async Task<WorkBoardDetailResponse> GetDetailWithoutVisitAsync(
        WorkBoard board,
        Guid memberId,
        CancellationToken cancellationToken)
    {
        var refreshed = await db.WorkBoards.AsNoTracking()
            .Include(x => x.Columns.OrderBy(column => column.Position))
            .SingleAsync(x => x.Id == board.Id, cancellationToken);
        return await ToDetailAsync(refreshed, memberId, cancellationToken);
    }

    private async Task EnforceWipLimitAsync(
        Guid boardId,
        WorkBoardColumn column,
        Guid? excludedItemId,
        CancellationToken cancellationToken)
    {
        if (column.WipPolicy != WorkBoardWipPolicy.HardLimit || !column.WipLimit.HasValue)
            return;
        var count = await db.CoreWorkTasks.CountAsync(x =>
            x.BoardId == boardId &&
            x.BoardColumnId == column.Id &&
            (!excludedItemId.HasValue || x.Id != excludedItemId), cancellationToken);
        if (count >= column.WipLimit.Value)
            throw new InvalidOperationException(
                $"Column '{column.Name}' has reached its WIP limit of {column.WipLimit.Value}.");
    }

    private static long RankBefore(List<WorkTask> targetItems, Guid? beforeItemId)
    {
        if (!beforeItemId.HasValue)
            return (targetItems.LastOrDefault()?.BoardRank ?? 0) + 1024;
        var index = targetItems.FindIndex(x => x.Id == beforeItemId.Value);
        if (index < 0)
            throw new ArgumentException("The reference work item is not in the target column.");
        var before = targetItems[index].BoardRank;
        var previous = index == 0 ? 0 : targetItems[index - 1].BoardRank;
        if (before - previous > 1)
            return previous + ((before - previous) / 2);

        var now = DateTimeOffset.UtcNow;
        for (var position = 0; position < targetItems.Count; position++)
        {
            targetItems[position].BoardRank = (position + 1L) * 1024;
            targetItems[position].Revision++;
            targetItems[position].UpdatedAt = now;
        }
        before = targetItems[index].BoardRank;
        previous = index == 0 ? 0 : targetItems[index - 1].BoardRank;
        return previous + ((before - previous) / 2);
    }

    private static T ParseEnum<T>(string value, string label) where T : struct, Enum =>
        Enum.TryParse<T>(value, true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new ArgumentException($"The {label} '{value}' is invalid.");

    private static WorkTaskStatus StatusFor(WorkBoardColumnCategory category) => category switch
    {
        WorkBoardColumnCategory.ToDo => WorkTaskStatus.Ready,
        WorkBoardColumnCategory.InProgress => WorkTaskStatus.Running,
        WorkBoardColumnCategory.Done => WorkTaskStatus.Completed,
        WorkBoardColumnCategory.Cancelled => WorkTaskStatus.Cancelled,
        _ => WorkTaskStatus.Ready
    };

    private static WorkBoardItemResponse ToItemResponse(WorkTask item) => new(
        item.Id,
        item.BoardId!.Value,
        item.BoardColumnId!.Value,
        item.ParentWorkTaskId,
        item.SprintId,
        item.Kind.ToString(),
        item.Title,
        item.Description,
        item.Status.ToString(),
        item.Priority.ToString(),
        item.EstimatePoints,
        item.BoardRank,
        item.Revision,
        item.DueDate,
        item.CreatedAt,
        item.UpdatedAt)
    {
        Identifier = item.Identifier,
        AccountableOrganizationUserId = item.AccountableOrganizationUserId,
        StageAssignments = item.StageAssignments.Select(ToAssignmentContract).ToList()
    };

    private static CSweet.WorkManagement.Contracts.WorkStageAssignment ToAssignmentContract(
        WorkItemStageAssignment assignment) => new(
            assignment.StageKey,
            assignment.PrincipalKind.ToString(),
            assignment.OrganizationUserId,
            assignment.AgentInstallationId,
            assignment.PlatformAction);

    private static void ValidateStageAssignments(
        bool executable,
        IReadOnlyList<WorkOrchestrationStage> stages,
        IReadOnlyList<CSweet.WorkManagement.Contracts.WorkStageAssignment> assignments)
    {
        if (assignments.Select(x => x.StageKey).Distinct(StringComparer.Ordinal).Count() != assignments.Count)
            throw new ArgumentException("A stage may have only one assignment.");
        var stageByKey = stages.ToDictionary(x => x.Key, StringComparer.Ordinal);
        foreach (var assignment in assignments)
        {
            if (!stageByKey.TryGetValue(assignment.StageKey, out var stage))
                throw new ArgumentException($"Stage '{assignment.StageKey}' is not in the published policy.");
            var kind = ParseEnum<WorkOrchestrationPrincipalKind>(
                assignment.PrincipalKind, "stage assignment principal kind");
            if (stage.Type == WorkOrchestrationStageType.AgentExecution &&
                (kind != WorkOrchestrationPrincipalKind.AgentInstallation || !assignment.AgentInstallationId.HasValue))
                throw new ArgumentException($"Agent stage '{stage.Key}' requires an exact agent installation.");
            if (stage.Type == WorkOrchestrationStageType.ManualWork &&
                (kind != WorkOrchestrationPrincipalKind.Human || !assignment.OrganizationUserId.HasValue))
                throw new ArgumentException($"Manual stage '{stage.Key}' requires a human organization user.");
            if (stage.Type == WorkOrchestrationStageType.MemberExecution &&
                !((kind == WorkOrchestrationPrincipalKind.Human && assignment.OrganizationUserId.HasValue) ||
                  (kind == WorkOrchestrationPrincipalKind.AgentInstallation && assignment.AgentInstallationId.HasValue)))
                throw new ArgumentException($"Member stage '{stage.Key}' requires an exact human or agent assignee.");
            if (stage.Type == WorkOrchestrationStageType.ManagerApproval &&
                kind != WorkOrchestrationPrincipalKind.BoardManager)
                throw new ArgumentException($"Approval stage '{stage.Key}' must be assigned to the board manager.");
            if (stage.Type == WorkOrchestrationStageType.TrustedPlatformAction &&
                (kind != WorkOrchestrationPrincipalKind.PlatformAction || string.IsNullOrWhiteSpace(assignment.PlatformAction)))
                throw new ArgumentException($"Platform stage '{stage.Key}' requires a registered platform action.");
        }
        if (!executable) return;
        var required = stages.Where(x => x.Type is
                WorkOrchestrationStageType.AgentExecution or
                WorkOrchestrationStageType.ManualWork or
                WorkOrchestrationStageType.MemberExecution or
                WorkOrchestrationStageType.ManagerApproval or
                WorkOrchestrationStageType.TrustedPlatformAction)
            .Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        if (!required.SetEquals(assignments.Select(x => x.StageKey)))
            throw new ArgumentException("Executable work items require one assignment for every work and approval stage.");
    }

    private async Task ValidateManagerAsync(
        Guid organizationId,
        Guid organizationUserId,
        CancellationToken cancellationToken)
    {
        if (!await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
                x.Id == organizationUserId && x.OrganizationId == organizationId && x.IsActive,
                cancellationToken))
            throw new ArgumentException("The selected organization user is not active in this organization.");
    }

    private async Task<string> ResolveBoardKeyAsync(
        Guid organizationId,
        string? requested,
        string name,
        Guid? excludedBoardId,
        CancellationToken cancellationToken)
    {
        var seed = string.IsNullOrWhiteSpace(requested)
            ? new string(name.Where(char.IsLetterOrDigit).Take(6).ToArray())
            : requested.Trim();
        seed = seed.ToUpperInvariant();
        if (seed.Length < 2) seed = $"B{seed}1";
        if (!Regex.IsMatch(seed, "^[A-Z][A-Z0-9]{1,11}$"))
            throw new ArgumentException("Board key must be 2-12 uppercase letters or digits and begin with a letter.");
        var candidate = seed;
        var suffix = 2;
        while (await db.WorkBoards.AsNoTracking().AnyAsync(x =>
                   x.OrganizationId == organizationId && x.Key == candidate &&
                   (!excludedBoardId.HasValue || x.Id != excludedBoardId.Value), cancellationToken))
        {
            var tail = suffix++.ToString();
            candidate = $"{seed[..Math.Min(seed.Length, 12 - tail.Length)]}{tail}";
        }
        return candidate;
    }

    private async Task<bool> SetArchiveStateAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        bool archive,
        CancellationToken cancellationToken)
    {
        var member = await ResolveMemberAsync(organizationId, applicationUserId, cancellationToken);
        var action = archive ? WorkBoardActions.Archive : WorkBoardActions.Restore;
        var decision = await RequireAsync(organizationId, member, action, boardId, cancellationToken);
        var board = await db.WorkBoards.SingleOrDefaultAsync(x =>
            x.Id == boardId && x.OrganizationId == organizationId && !x.IsPersonalTodo,
            cancellationToken);
        if (board is null) return false;
        if (archive && board.IsDefault)
            throw new InvalidOperationException("The default board cannot be archived.");
        board.ArchivedAt = archive ? DateTimeOffset.UtcNow : null;
        board.Revision++;
        board.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await WriteAllowedAsync(
            organizationId, member, action, board.Id, decision.GrantId!.Value,
            new { board.Id, archived = archive }, cancellationToken);
        return true;
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
        DateTimeOffset occurredAt) =>
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
            DataJson = JsonSerializer.Serialize(data, JsonOptions),
            OccurredAt = occurredAt
        });

    private async Task QueueRealtimeAsync(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        string changeType,
        long revision,
        CancellationToken cancellationToken)
    {
        var recipients = await ResolveRealtimeRecipientsAsync(
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
                itemId,
                changeType,
                revision
            }, JsonOptions),
            Status = ApplicationRealtimeOutboxStatus.Pending,
            NextAttemptAt = now,
            OccurredAt = now
        });
    }

    private async Task<IReadOnlyList<Guid>> ResolveRealtimeRecipientsAsync(
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
                 x.Action == WorkItemActions.Read))
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

    private static string EventTypeFor(string action) => action switch
    {
        WorkItemActions.Complete => "item.completed",
        WorkItemActions.Cancel => "item.cancelled",
        WorkItemActions.Reopen => "item.reopened",
        _ => "item.moved"
    };

    private async Task<OrganizationUser> ResolveMemberAsync(
        Guid organizationId,
        Guid applicationUserId,
        CancellationToken cancellationToken) =>
        await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.ApplicationUserId == applicationUserId &&
            x.EmployeeType == EmployeeType.Human &&
            x.IsActive, cancellationToken)
        ?? throw new UnauthorizedAccessException("The current user is not an active human member of this organization.");

    private async Task<ScopedAuthorizationDecision> RequireAsync(
        Guid organizationId,
        OrganizationUser member,
        string action,
        Guid? boardId,
        CancellationToken cancellationToken)
    {
        await WorkBoardProvisioning.EnsureLegacyGrantsAsync(db, organizationId, member, cancellationToken);
        var decision = await authorization.AuthorizeAsync(
            organizationId,
            GrantSubjectKind.OrganizationUser,
            member.Id,
            action,
            boardId.HasValue ? GrantScopeKind.Board : GrantScopeKind.Organization,
            boardId,
            cancellationToken);
        if (decision.Allowed) return decision;
        await WriteDeniedAsync(organizationId, member, action, boardId, cancellationToken);
        throw new UnauthorizedAccessException($"The current user does not have the required '{action}' grant.");
    }

    private async Task ValidateRequestAsync(
        Guid organizationId,
        string name,
        Guid? workstreamId,
        Guid? teamId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Board name is required.", nameof(name));
        if (workstreamId.HasValue && !await db.Workstreams.AnyAsync(x =>
                x.Id == workstreamId && x.OrganizationId == organizationId, cancellationToken))
            throw new ArgumentException("The selected workstream does not belong to this organization.", nameof(workstreamId));
        if (teamId.HasValue && !await db.OrganizationTeams.AnyAsync(x =>
                x.Id == teamId && x.OrganizationId == organizationId && x.ArchivedAt == null,
                cancellationToken))
            throw new ArgumentException("The selected team is not active in this organization.", nameof(teamId));
    }

    private async Task<ScopedAuthorizationDecision> RequireCreateAsync(
        Guid organizationId,
        OrganizationUser member,
        Guid? teamId,
        CancellationToken cancellationToken)
    {
        await WorkBoardProvisioning.EnsureLegacyGrantsAsync(db, organizationId, member, cancellationToken);
        var decision = await authorization.AuthorizeAsync(
            organizationId,
            GrantSubjectKind.OrganizationUser,
            member.Id,
            WorkBoardActions.Create,
            teamId.HasValue ? GrantScopeKind.Team : GrantScopeKind.Organization,
            teamId,
            cancellationToken);
        if (decision.Allowed) return decision;
        await WriteDeniedAsync(organizationId, member, WorkBoardActions.Create, null, cancellationToken);
        throw new UnauthorizedAccessException(
            teamId.HasValue
                ? "The current user does not have a create grant for the selected team."
                : "The current user does not have an organization board-create grant.");
    }

    private async Task<WorkBoardDetailResponse> ToDetailAsync(
        WorkBoard board,
        Guid memberId,
        CancellationToken cancellationToken)
    {
        var grants = await ActiveGrantsAsync(board.OrganizationId, memberId, cancellationToken);
        var all = await ActiveOrganizationGrantsAsync(board.OrganizationId, cancellationToken);
        var preference = await db.WorkBoardUserPreferences.AsNoTracking().SingleOrDefaultAsync(x =>
            x.BoardId == board.Id && x.OrganizationUserId == memberId, cancellationToken);
        var count = await db.CoreWorkTasks.CountAsync(x =>
            x.BoardId == board.Id &&
            x.Status != WorkTaskStatus.Completed &&
            x.Status != WorkTaskStatus.Cancelled, cancellationToken);
        var canReadItems = HasActionForBoard(grants, WorkItemActions.Read, board.Id);
        var itemRows = canReadItems
            ? await db.CoreWorkTasks.AsNoTracking()
                .Where(x => x.BoardId == board.Id && x.BoardColumnId != null)
                .OrderBy(x => x.BoardColumnId)
                .ThenBy(x => x.BoardRank)
                .Select(x => new
                {
                    x.Id,
                    x.BoardColumnId,
                    x.ParentWorkTaskId,
                    x.SprintId,
                    x.Kind,
                    x.Title,
                    x.Description,
                    x.Status,
                    x.Priority,
                    x.EstimatePoints,
                    x.BoardRank,
                    x.Revision,
                    x.DueDate,
                    x.CreatedAt,
                    x.UpdatedAt
                    ,x.Identifier,
                    x.AccountableOrganizationUserId
                })
                .ToListAsync(cancellationToken)
            : [];
        var items = itemRows
            .Select(x => new WorkBoardItemResponse(
                x.Id,
                board.Id,
                x.BoardColumnId!.Value,
                x.ParentWorkTaskId,
                x.SprintId,
                x.Kind.ToString(),
                x.Title,
                x.Description,
                x.Status.ToString(),
                x.Priority.ToString(),
                x.EstimatePoints,
                x.BoardRank,
                x.Revision,
                x.DueDate,
                x.CreatedAt,
                x.UpdatedAt)
            {
                Identifier = x.Identifier,
                AccountableOrganizationUserId = x.AccountableOrganizationUserId
            })
            .ToList();
        return new WorkBoardDetailResponse(
            ToSummary(board, memberId, grants, all, preference?.IsFavorite ?? false,
                preference?.LastVisitedAt, count),
            board.Columns.OrderBy(x => x.Position)
                .Select(x => new WorkBoardColumnResponse(
                    x.Id, x.Name, x.Category.ToString(), x.Position, x.WipPolicy.ToString(), x.WipLimit))
                .ToList(),
            items);
    }

    private static WorkBoardSummaryResponse ToSummary(
        WorkBoard board,
        Guid memberId,
        IReadOnlyList<ScopedActionGrant> memberGrants,
        IReadOnlyList<ScopedActionGrant> allGrants,
        bool favorite,
        DateTimeOffset? lastVisitedAt,
        int activeItemCount)
    {
        var allowed = memberGrants
            .Where(x => x.ScopeKind == GrantScopeKind.Organization ||
                        (x.ScopeKind == GrantScopeKind.Board && x.ScopeId == board.Id))
            .Select(x => x.Action)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        if (!HasActionForBoard(memberGrants, WorkItemActions.Read, board.Id))
            activeItemCount = 0;
        var grantedSubjects = allGrants
            .Where(x => x.Action == WorkBoardActions.Read &&
                        (x.ScopeKind == GrantScopeKind.Organization ||
                         (x.ScopeKind == GrantScopeKind.Board && x.ScopeId == board.Id)))
            .Select(x => (x.SubjectKind, x.SubjectId))
            .Distinct()
            .Count();
        return new WorkBoardSummaryResponse(
            board.Id, board.OrganizationId, board.WorkstreamId,
            board.Name, board.Description, board.IsDefault, board.ArchivedAt.HasValue,
            favorite, lastVisitedAt, activeItemCount, grantedSubjects,
            board.Revision,
            board.CreatedAt, board.UpdatedAt, allowed)
        {
            TeamId = board.TeamId,
            ManagerOrganizationUserId = board.ManagerOrganizationUserId,
            Key = board.Key
        };
    }

    private static bool HasActionForBoard(
        IReadOnlyList<ScopedActionGrant> grants,
        string action,
        Guid boardId) =>
        grants.Any(x =>
            x.Action == action &&
            (x.ScopeKind == GrantScopeKind.Organization ||
             (x.ScopeKind == GrantScopeKind.Board && x.ScopeId == boardId)));

    private async Task<List<ScopedActionGrant>> ActiveGrantsAsync(
        Guid organizationId,
        Guid memberId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.ScopedActionGrants.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId &&
                        x.SubjectKind == GrantSubjectKind.OrganizationUser &&
                        x.SubjectId == memberId &&
                        x.RevokedAt == null &&
                        (!x.ExpiresAt.HasValue || x.ExpiresAt > now))
            .ToListAsync(cancellationToken);
    }

    private async Task<List<ScopedActionGrant>> ActiveOrganizationGrantsAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.ScopedActionGrants.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId &&
                        x.RevokedAt == null &&
                        (!x.ExpiresAt.HasValue || x.ExpiresAt > now))
            .ToListAsync(cancellationToken);
    }

    private Task WriteDeniedAsync(
        Guid organizationId,
        OrganizationUser member,
        string action,
        Guid? boardId,
        CancellationToken cancellationToken) =>
        audit.AppendAsync(new AuditEventWriteRequest(
            "work.authorization.denied",
            "WorkManagement",
            "Inbound",
            "Denied",
            organizationId,
            boardId.HasValue ? "WorkBoard" : "Organization",
            boardId ?? organizationId,
            $"Denied {action}.",
            MetadataJson: JsonSerializer.Serialize(new { action, boardId }),
            Actor: new AuditActor(
                "Human", true, member.ApplicationUserId, member.Id, member.DisplayName),
            ErrorCode: "grant_required"),
            cancellationToken);

    private Task WriteAllowedAsync(
        Guid organizationId,
        OrganizationUser member,
        string action,
        Guid? boardId,
        Guid grantId,
        object metadata,
        CancellationToken cancellationToken) =>
        audit.AppendAsync(new AuditEventWriteRequest(
            action,
            "WorkManagement",
            "Inbound",
            "Completed",
            organizationId,
            boardId.HasValue ? "WorkBoard" : "Organization",
            boardId ?? organizationId,
            $"Completed {action}.",
            MetadataJson: JsonSerializer.Serialize(new { action, grantId, data = metadata }),
            Actor: new AuditActor(
                "Human", true, member.ApplicationUserId, member.Id, member.DisplayName)),
            cancellationToken);

    private static WorkBoardColumn NewColumn(
        string name,
        WorkBoardColumnCategory category,
        int position) => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Category = category,
            Position = position,
            WipPolicy = WorkBoardWipPolicy.Disabled
        };
}
