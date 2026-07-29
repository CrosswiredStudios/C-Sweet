using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

internal static class WorkBoardProvisioning
{
    public static async Task<WorkBoard> EnsureDefaultBoardAsync(
        CSweetDbContext db,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        var board = await db.WorkBoards
            .Include(x => x.Columns)
            .OrderByDescending(x => x.IsDefault)
            .ThenBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.ArchivedAt == null, cancellationToken);

        if (board is null)
        {
            var now = DateTimeOffset.UtcNow;
            board = new WorkBoard
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                Name = "Company work",
                Description = "The default board for company-wide work.",
                IsDefault = true,
                CreatedAt = now,
                UpdatedAt = now,
                Columns =
                [
                    NewColumn("To Do", WorkBoardColumnCategory.ToDo, 0),
                    NewColumn("Done", WorkBoardColumnCategory.Done, 1)
                ]
            };
            db.WorkBoards.Add(board);
        }
        else if (!board.IsDefault)
        {
            board.IsDefault = true;
            board.UpdatedAt = DateTimeOffset.UtcNow;
        }

        var unassigned = await db.CoreWorkTasks
            .Where(x => x.OrganizationId == organizationId && x.BoardId == null)
            .ToListAsync(cancellationToken);
        foreach (var task in unassigned)
            task.BoardId = board.Id;

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(cancellationToken);

        await EnsureTaskPlacementAsync(db, board, cancellationToken);

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(cancellationToken);

        return board;
    }

    public static async Task EnsureLegacyGrantsAsync(
        CSweetDbContext db,
        Guid organizationId,
        OrganizationUser requestingMember,
        CancellationToken cancellationToken)
    {
        if (requestingMember.PermissionLevel != OrganizationPermissionLevel.Owner)
            return;

        var hasBoardGrants = await db.ScopedActionGrants.AnyAsync(x =>
            x.OrganizationId == organizationId &&
            x.Action.StartsWith("work.board.") &&
            x.RevokedAt == null, cancellationToken);
        var hasItemGrants = await db.ScopedActionGrants.AnyAsync(x =>
            x.OrganizationId == organizationId &&
            x.Action.StartsWith("work.item.") &&
            x.RevokedAt == null, cancellationToken);
        var hasSprintGrants = await db.ScopedActionGrants.AnyAsync(x =>
            x.OrganizationId == organizationId &&
            x.Action.StartsWith("work.sprint.") &&
            x.RevokedAt == null, cancellationToken);
        var hasAutomationGrants = await db.ScopedActionGrants.AnyAsync(x =>
            x.OrganizationId == organizationId &&
            x.Action.StartsWith("work.automation.") &&
            x.RevokedAt == null, cancellationToken);
        var hasGrantManagement = await db.ScopedActionGrants.AnyAsync(x =>
            x.OrganizationId == organizationId &&
            x.Action == WorkBoardActions.ManageGrants &&
            x.RevokedAt == null, cancellationToken);
        if (hasBoardGrants && hasItemGrants && hasSprintGrants &&
            hasAutomationGrants && hasGrantManagement)
            return;

        var members = await db.CoreOrganizationUsers
            .Where(x => x.OrganizationId == organizationId && x.IsActive)
            .ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var member in members)
        {
            var boardActions = member.PermissionLevel switch
            {
                OrganizationPermissionLevel.Owner => WorkBoardActions.All,
                OrganizationPermissionLevel.Manager => WorkBoardActions.All
                    .Where(x => x != WorkBoardActions.ManageGrants).ToList(),
                _ => [WorkBoardActions.Read]
            };
            var itemActions = member.PermissionLevel switch
            {
                OrganizationPermissionLevel.Owner or OrganizationPermissionLevel.Manager => WorkItemActions.All,
                _ => [WorkItemActions.Read]
            };
            var sprintActions = member.PermissionLevel switch
            {
                OrganizationPermissionLevel.Owner or OrganizationPermissionLevel.Manager =>
                    WorkSprintActions.All,
                _ => [WorkSprintActions.Read]
            };
            var automationActions = member.PermissionLevel switch
            {
                OrganizationPermissionLevel.Owner or OrganizationPermissionLevel.Manager =>
                    WorkAutomationActions.All,
                _ => [WorkAutomationActions.Read]
            };
            var missingManagementActions =
                member.PermissionLevel == OrganizationPermissionLevel.Owner &&
                !hasGrantManagement
                    ? new[] { WorkBoardActions.ManageGrants }
                    : [];
            var actions = (hasBoardGrants ? missingManagementActions : boardActions)
                .Concat(hasItemGrants ? [] : itemActions)
                .Concat(hasSprintGrants ? [] : sprintActions)
                .Concat(hasAutomationGrants ? [] : automationActions);
            foreach (var action in actions)
            {
                db.ScopedActionGrants.Add(new ScopedActionGrant
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = organizationId,
                    SubjectKind = GrantSubjectKind.OrganizationUser,
                    SubjectId = member.Id,
                    Action = action,
                    ScopeKind = GrantScopeKind.Organization,
                    CanDelegate = member.PermissionLevel == OrganizationPermissionLevel.Owner,
                    GrantedBySubjectKind = GrantSubjectKind.OrganizationUser,
                    GrantedBySubjectId = requestingMember.Id,
                    GrantedAt = now
                });
            }
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task EnsureTaskPlacementAsync(
        CSweetDbContext db,
        WorkBoard board,
        CancellationToken cancellationToken)
    {
        if (board.Columns.Count == 0)
        {
            List<WorkBoardColumn> columns =
            [
                NewColumn("To Do", WorkBoardColumnCategory.ToDo, 0),
                NewColumn("Done", WorkBoardColumnCategory.Done, 1)
            ];
            foreach (var column in columns)
            {
                column.BoardId = board.Id;
                db.WorkBoardColumns.Add(column);
            }
            board.Columns = columns;
        }

        var tasks = await db.CoreWorkTasks
            .Where(x => x.BoardId == board.Id && x.BoardColumnId == null)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        if (tasks.Count == 0) return;

        var ranks = await db.CoreWorkTasks
            .Where(x => x.BoardId == board.Id && x.BoardColumnId != null)
            .GroupBy(x => x.BoardColumnId!.Value)
            .Select(x => new { ColumnId = x.Key, Rank = x.Max(item => item.BoardRank) })
            .ToDictionaryAsync(x => x.ColumnId, x => x.Rank, cancellationToken);
        foreach (var task in tasks)
        {
            var preferredCategory = task.Status switch
            {
                WorkTaskStatus.Completed or WorkTaskStatus.Cancelled => WorkBoardColumnCategory.Done,
                WorkTaskStatus.Assigned or WorkTaskStatus.Running or
                    WorkTaskStatus.WaitingForApproval => WorkBoardColumnCategory.InProgress,
                _ => WorkBoardColumnCategory.ToDo
            };
            var column = board.Columns
                .OrderBy(x => x.Position)
                .FirstOrDefault(x => x.Category == preferredCategory)
                ?? board.Columns.OrderBy(x => x.Position)
                    .First(x => x.Category is WorkBoardColumnCategory.ToDo or WorkBoardColumnCategory.Done);
            var nextRank = ranks.GetValueOrDefault(column.Id) + 1024;
            task.BoardColumnId = column.Id;
            task.BoardRank = nextRank;
            task.Revision++;
            ranks[column.Id] = nextRank;
        }
    }

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
