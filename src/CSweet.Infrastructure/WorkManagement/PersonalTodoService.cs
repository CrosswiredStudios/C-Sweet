using System.Text.Json;
using System.Diagnostics.Metrics;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.Communications;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public sealed class WorkItemMutationEngine(CSweetDbContext db, TimeProvider clock) : IWorkItemMutationEngine
{
    private static readonly TimeSpan ClaimDuration = TimeSpan.FromMinutes(5);
    private const int SoftOpenItemLimit = 100;
    private const int HardOpenItemLimit = 250;
    private const int BlockedNotificationTitleExcerptLength = 96;
    private const int BlockedNotificationReasonExcerptLength = 120;
    private static readonly Meter Meter = new("CSweet.Application.PersonalWork");
    private static readonly Counter<long> SoftLimitWarnings = Meter.CreateCounter<long>("csweet.personal_work.soft_limit_warnings");
    private static readonly Counter<long> HardLimitRejections = Meter.CreateCounter<long>("csweet.personal_work.hard_limit_rejections");
    private static readonly IReadOnlySet<string> OwnerActions = new HashSet<string>(
        [PersonalTodoActions.Read, PersonalTodoActions.Add, PersonalTodoActions.Reorder, PersonalTodoActions.Requeue,
         PersonalTodoActions.Activate,
         PersonalTodoActions.Claim, PersonalTodoActions.Complete, PersonalTodoActions.Block,
         PersonalTodoActions.Release, PersonalTodoActions.Update,
         PersonalTodoActions.Archive, PersonalTodoActions.Restore], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> HumanOwnerActions = new HashSet<string>(
        [PersonalTodoActions.Read, PersonalTodoActions.Add, PersonalTodoActions.Reorder,
         PersonalTodoActions.Activate,
         PersonalTodoActions.Requeue, PersonalTodoActions.Complete, PersonalTodoActions.Block,
         PersonalTodoActions.Release, PersonalTodoActions.Update,
         PersonalTodoActions.Archive, PersonalTodoActions.Restore], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> ManagerActions = new HashSet<string>(
        [PersonalTodoActions.Read, PersonalTodoActions.Add, PersonalTodoActions.Reorder,
         PersonalTodoActions.Activate,
         PersonalTodoActions.Requeue], StringComparer.Ordinal);

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var activeOwners = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.IsActive)
            .Select(x => new { x.OrganizationId, x.Id })
            .ToListAsync(cancellationToken);
        foreach (var owner in activeOwners)
            await EnsureBoardAsync(owner.OrganizationId, owner.Id, cancellationToken);

        var activeOwnerIds = activeOwners.Select(x => x.Id).ToHashSet();
        var inactiveBoardIds = await db.WorkBoards.AsNoTracking()
            .Where(x => x.Kind == WorkBoardKind.Personal &&
                (!x.OwnerOrganizationUserId.HasValue ||
                 !activeOwnerIds.Contains(x.OwnerOrganizationUserId.Value)))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var now = clock.GetUtcNow();
        if (inactiveBoardIds.Count > 0)
        {
            var inactiveGrants = await db.ScopedActionGrants.Where(x =>
                x.ScopeKind == GrantScopeKind.Board && x.ScopeId.HasValue &&
                inactiveBoardIds.Contains(x.ScopeId.Value) && x.RevokedAt == null &&
                (PersonalTodoActions.All.Contains(x.Action) || x.Action == WorkItemActions.Transfer))
                .ToListAsync(cancellationToken);
            foreach (var grant in inactiveGrants)
            {
                grant.RevokedAt = now;
                grant.Revision++;
            }
        }

        var expired = await db.CoreWorkTasks
            .Include(x => x.Board)
            .Where(x => x.Board != null && x.Board.Kind == WorkBoardKind.Personal &&
                x.Status == WorkTaskStatus.Running && x.ClaimExpiresAt < now)
            .ToListAsync(cancellationToken);
        var replacementWakeItemIds = new HashSet<Guid>();
        foreach (var item in expired)
        {
            var owner = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
                x.Id == item.Board!.OwnerOrganizationUserId && x.IsActive &&
                x.AgentInstallationId != null, cancellationToken);
            if (owner is null)
                continue;
            var todoColumnId = await db.WorkBoardColumns.AsNoTracking()
                .Where(x => x.BoardId == item.BoardId && x.Category == WorkBoardColumnCategory.ToDo)
                .Select(x => x.Id).SingleAsync(cancellationToken);
            item.Status = WorkTaskStatus.Ready;
            item.BoardColumnId = todoColumnId;
            item.ClaimEventId = null;
            item.ClaimExpiresAt = null;
            item.Revision++;
            item.UpdatedAt = now;
            QueueAvailable(item.OrganizationId, owner, item.BoardId!.Value, item.Id, now);
            replacementWakeItemIds.Add(item.Id);
        }

        var strandedReady = await db.CoreWorkTasks.AsNoTracking()
            .Where(x => x.Board != null && x.Board.Kind == WorkBoardKind.Personal &&
                x.Board.OwnerOrganizationUserId.HasValue && x.ArchivedAt == null &&
                x.Status == WorkTaskStatus.Ready)
            .Select(x => new
            {
                x.Id,
                x.OrganizationId,
                x.BoardId,
                OwnerId = x.Board!.OwnerOrganizationUserId!.Value
            })
            .ToListAsync(cancellationToken);
        foreach (var ready in strandedReady.Where(x => !replacementWakeItemIds.Contains(x.Id)))
        {
            var owner = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == ready.OwnerId && x.IsActive && x.EmployeeType == EmployeeType.Agent &&
                x.AgentInstallationId != null, cancellationToken);
            if (owner is null) continue;
            var prefix = $"personal-todo-available:{ready.Id:N}:";
            var hasPendingWake = await db.AgentPlatformEventOutbox.AsNoTracking().AnyAsync(x =>
                x.OrganizationId == ready.OrganizationId &&
                x.EventType == Wire.PersonalTodoEvents.Available &&
                x.Status == AgentPlatformEventOutboxStatus.Pending &&
                x.IdempotencyKey.StartsWith(prefix), cancellationToken);
            if (hasPendingWake) continue;
            var lastWake = await db.AgentPlatformEventOutbox.AsNoTracking()
                .Where(x => x.OrganizationId == ready.OrganizationId &&
                    x.EventType == Wire.PersonalTodoEvents.Available &&
                    x.IdempotencyKey.StartsWith(prefix))
                .MaxAsync(x => (DateTimeOffset?)x.OccurredAt, cancellationToken);
            if (lastWake.HasValue && lastWake.Value > now.AddMinutes(-1)) continue;
            QueueAvailable(ready.OrganizationId, owner, ready.BoardId!.Value, ready.Id, now);
        }
        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(cancellationToken);
    }

    public async Task EnsureBoardAsync(Guid organizationId, Guid ownerOrganizationUserId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var owner = await db.CoreOrganizationUsers
            .SingleOrDefaultAsync(x => x.Id == ownerOrganizationUserId &&
                x.OrganizationId == organizationId && x.IsActive,
                cancellationToken)
            ?? throw new ArgumentException("The personal-board owner must be an active employee.");
        var board = await db.WorkBoards.Include(x => x.Columns)
            .SingleOrDefaultAsync(x => x.OwnerOrganizationUserId == owner.Id,
                cancellationToken);
        var now = clock.GetUtcNow();
        if (board is null)
        {
            board = new WorkBoard
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId,
                OwnerOrganizationUserId = owner.Id, Kind = WorkBoardKind.Personal,
                ManagerOrganizationUserId = owner.ReportsToOrganizationUserId,
                Key = PersonalKey(owner.Id), Name = PersonalName(owner.DisplayName),
                Description = "Protected personal work queue.", CreatedAt = now, UpdatedAt = now,
                Columns =
                [
                    NewColumn("To Do", WorkBoardColumnCategory.ToDo, 0),
                    NewColumn("Doing", WorkBoardColumnCategory.InProgress, 1),
                    NewColumn("Blocked", WorkBoardColumnCategory.Blocked, 2),
                    NewColumn("Done", WorkBoardColumnCategory.Done, 3)
                ]
            };
            db.WorkBoards.Add(board);
        }
        else
        {
            board.Kind = WorkBoardKind.Personal;
            if (!board.Columns.Any(x => x.Category == WorkBoardColumnCategory.Blocked))
                board.Columns.Add(NewColumn("Blocked", WorkBoardColumnCategory.Blocked,
                    board.Columns.Count));
            var expectedName = PersonalName(owner.DisplayName);
            if (board.ManagerOrganizationUserId != owner.ReportsToOrganizationUserId ||
                board.Name != expectedName)
            {
                board.ManagerOrganizationUserId = owner.ReportsToOrganizationUserId;
                board.Name = expectedName;
                board.Revision++;
                board.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await ReconcileGrantsAsync(board, owner, cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
    }

    public async Task<Wire.PersonalTodoDirectory> ListAsync(
        Guid organizationId, PersonalTodoActor actor, bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        await EnsureActorAsync(organizationId, actor, cancellationToken);
        var owners = (await AccessibleOwnerIdsAsync(organizationId, actor.OrganizationUserId,
            cancellationToken)).ToList();
        foreach (var ownerId in owners)
            await EnsureBoardAsync(organizationId, ownerId, cancellationToken);

        var boards = await db.WorkBoards.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Kind == WorkBoardKind.Personal &&
                x.OwnerOrganizationUserId.HasValue && owners.Contains(x.OwnerOrganizationUserId.Value))
            .Include(x => x.Columns)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
        var results = new List<Wire.PersonalTodoBoard>();
        foreach (var board in boards)
        {
            await RequireGrantAsync(organizationId, board.Id, actor, PersonalTodoActions.Read, cancellationToken);
            results.Add(await MapBoardAsync(board, includeArchived, cancellationToken));
        }
        return new Wire.PersonalTodoDirectory(results, actor.OrganizationUserId);
    }

    public async Task<Wire.PersonalTodoItem> AddAsync(
        Guid organizationId, PersonalTodoActor actor, Wire.AddPersonalTodoItemRequest request,
        CancellationToken cancellationToken = default)
    {
        var actorUser = await EnsureActorAsync(organizationId, actor, cancellationToken);
        var ownerId = request.TargetOrganizationUserId ?? actor.OrganizationUserId;
        var owner = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
            x.Id == ownerId && x.OrganizationId == organizationId && x.IsActive, cancellationToken)
            ?? throw new ArgumentException("The personal-board target is not an active employee.");
        var accessibleOwners = await AccessibleOwnerIdsAsync(organizationId,
            actor.OrganizationUserId, cancellationToken);
        if (!accessibleOwners.Contains(owner.Id))
            throw new UnauthorizedAccessException("Personal work may only be added for yourself or a reporting descendant.");
        await EnsureBoardAsync(organizationId, owner.Id, cancellationToken);
        var board = await PersonalBoardAsync(organizationId, owner.Id, cancellationToken);
        await RequireGrantAsync(organizationId, board.Id, actor, PersonalTodoActions.Add, cancellationToken);
        ValidateAdd(request);

        var key = request.IdempotencyKey.Trim();
        var existing = await db.CoreWorkTasks.AsNoTracking().SingleOrDefaultAsync(x =>
            x.CreatedByOrganizationUserId == actor.OrganizationUserId &&
            x.CreationIdempotencyKey == key, cancellationToken);
        if (existing is not null) return await MapItemAsync(existing, cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            existing = await db.CoreWorkTasks.AsNoTracking().FirstOrDefaultAsync(x =>
                x.BoardId == board.Id && x.CreatedByOrganizationUserId == actor.OrganizationUserId &&
                x.CorrelationId == request.CorrelationId.Trim(), cancellationToken);
            if (existing is not null) return await MapItemAsync(existing, cancellationToken);
        }

        var openCount = await db.CoreWorkTasks.AsNoTracking().CountAsync(x =>
            x.BoardId == board.Id && x.ArchivedAt == null &&
            x.Status != WorkTaskStatus.Completed && x.Status != WorkTaskStatus.Cancelled,
            cancellationToken);
        if (openCount >= HardOpenItemLimit)
        {
            HardLimitRejections.Add(1, new KeyValuePair<string, object?>("organization.id", organizationId));
            throw new InvalidOperationException($"This personal board has reached its limit of {HardOpenItemLimit} open tasks.");
        }
        if (openCount >= SoftOpenItemLimit)
        {
            SoftLimitWarnings.Add(1, new KeyValuePair<string, object?>("organization.id", organizationId));
            await AddOpenLimitNotificationsAsync(owner, openCount + 1, cancellationToken);
        }

        if (request.SourceMessageId.HasValue || request.SourceConversationId.HasValue)
            await ValidateSourceAsync(organizationId, owner.Id, request, cancellationToken);
        var normalized = await WorkItemMentionCodec.NormalizeAndValidateAsync(
            db, organizationId, request.Title, request.Description, request.Mentions,
            cancellationToken);
        var todoColumn = board.Columns.Single(x => x.Category == WorkBoardColumnCategory.ToDo);
        var now = clock.GetUtcNow();
        var item = new WorkTask
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, BoardId = board.Id,
            BoardColumnId = todoColumn.Id, AssignedEmployeeId = owner.Id,
            AssignedAgentInstallationId = owner.AgentInstallationId,
            CreatedByOrganizationUserId = actorUser.Id,
            SourceConversationId = request.SourceConversationId,
            SourceMessageId = request.SourceMessageId,
            CorrelationId = request.CorrelationId?.Trim(),
            CausationId = request.CausationId?.Trim(),
            CreationIdempotencyKey = key,
            Title = normalized.Title, Description = normalized.Description,
            StructuredMentionsJson = normalized.MentionsJson,
            Kind = WorkItemKind.Task,
            Status = request.StartInBacklog ? WorkTaskStatus.Backlog : WorkTaskStatus.Ready,
            Priority = Enum.Parse<WorkTaskPriority>(request.Priority, true),
            DueDate = request.DueDate,
            BoardRank = (await db.CoreWorkTasks.Where(x => x.BoardId == board.Id)
                .Select(x => (long?)x.BoardRank).MaxAsync(cancellationToken) ?? 0) + 1024,
            CreatedAt = now, UpdatedAt = now
        };
        db.CoreWorkTasks.Add(item);
        if (item.Status == WorkTaskStatus.Ready)
            QueueAvailable(organizationId, owner, board.Id, item.Id, now);
        await AddManagerCreatedNotificationAsync(actorUser, owner, item, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await MapItemAsync(item, cancellationToken);
    }

    public async Task<Wire.PersonalTodoItem> ReorderAsync(
        Guid organizationId, PersonalTodoActor actor, Wire.ReorderPersonalTodoItemRequest request,
        CancellationToken cancellationToken = default)
    {
        var item = await LoadPersonalItemAsync(organizationId, request.ItemId, cancellationToken);
        await RequireGrantAsync(organizationId, item.BoardId!.Value, actor, PersonalTodoActions.Reorder, cancellationToken);
        if (item.Status != WorkTaskStatus.Ready)
            throw new InvalidOperationException("Only ready personal work can be reordered.");
        RequireRevision(item, request.ExpectedRevision);
        var ready = await db.CoreWorkTasks.Where(x => x.BoardId == item.BoardId && x.ArchivedAt == null &&
                x.Status == WorkTaskStatus.Ready && x.Id != item.Id)
            .OrderBy(x => x.BoardRank).ToListAsync(cancellationToken);
        var index = request.BeforeItemId.HasValue
            ? ready.FindIndex(x => x.Id == request.BeforeItemId.Value)
            : ready.Count;
        if (index < 0) throw new ArgumentException("The before item is not ready on this personal board.");
        ready.Insert(index, item);
        for (var i = 0; i < ready.Count; i++) ready[i].BoardRank = (i + 1L) * 1024;
        item.Revision++; item.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return await MapItemAsync(item, cancellationToken);
    }

    public async Task<Wire.PersonalTodoItem> RequeueAsync(
        Guid organizationId, PersonalTodoActor actor, Wire.RequeuePersonalTodoItemRequest request,
        CancellationToken cancellationToken = default)
    {
        var item = await LoadPersonalItemAsync(organizationId, request.ItemId, cancellationToken);
        await RequireGrantAsync(organizationId, item.BoardId!.Value, actor, PersonalTodoActions.Requeue, cancellationToken);
        var isWaitingInProgress = item.Status == WorkTaskStatus.Running &&
            !item.ClaimEventId.HasValue && !item.ClaimExpiresAt.HasValue;
        if (item.Status != WorkTaskStatus.Blocked && !isWaitingInProgress)
            throw new InvalidOperationException(
                "Only blocked or unclaimed in-progress personal work can be requeued.");
        RequireRevision(item, request.ExpectedRevision);
        var board = await db.WorkBoards.Include(x => x.Columns).SingleAsync(x => x.Id == item.BoardId, cancellationToken);
        item.Status = WorkTaskStatus.Ready;
        item.BoardColumnId = board.Columns.Single(x => x.Category == WorkBoardColumnCategory.ToDo).Id;
        item.BlockReason = null; item.ClaimEventId = null;
        item.ClaimExpiresAt = null; item.Revision++; item.UpdatedAt = clock.GetUtcNow();
        var owner = await OwnerAsync(board, cancellationToken);
        QueueAvailable(organizationId, owner, board.Id, item.Id, item.UpdatedAt);
        await db.SaveChangesAsync(cancellationToken);
        return await MapItemAsync(item, cancellationToken);
    }

    public async Task<Wire.PersonalTodoItem> ActivateAsync(
        Guid organizationId, PersonalTodoActor actor,
        Wire.ActivatePersonalTodoItemRequest request,
        CancellationToken cancellationToken = default)
    {
        var item = await LoadPersonalItemAsync(organizationId, request.ItemId, cancellationToken);
        await RequireGrantAsync(organizationId, item.BoardId!.Value, actor,
            PersonalTodoActions.Activate, cancellationToken);
        if (item.ArchivedAt.HasValue)
            throw new InvalidOperationException("Restore this personal task before activating it.");
        if (item.Status != WorkTaskStatus.Backlog)
            throw new InvalidOperationException("Only backlog personal work can be activated.");
        RequireRevision(item, request.ExpectedRevision);
        var board = await db.WorkBoards.Include(x => x.Columns)
            .SingleAsync(x => x.Id == item.BoardId, cancellationToken);
        item.Status = WorkTaskStatus.Ready;
        item.BoardColumnId = board.Columns.Single(x =>
            x.Category == WorkBoardColumnCategory.ToDo).Id;
        item.Revision++;
        item.UpdatedAt = clock.GetUtcNow();
        QueueAvailable(organizationId, await OwnerAsync(board, cancellationToken),
            board.Id, item.Id, item.UpdatedAt);
        await db.SaveChangesAsync(cancellationToken);
        return await MapItemAsync(item, cancellationToken);
    }

    public async Task<Wire.PersonalTodoItem> UpdateAsync(
        Guid organizationId, PersonalTodoActor actor, Wire.UpdatePersonalTodoItemRequest request,
        CancellationToken cancellationToken = default)
    {
        var item = await LoadPersonalItemAsync(organizationId, request.ItemId, cancellationToken);
        await RequireGrantAsync(organizationId, item.BoardId!.Value, actor,
            PersonalTodoActions.Update, cancellationToken);
        RequireRevision(item, request.ExpectedRevision);
        ValidateEditableFields(request.Title, request.Description, request.Priority);
        if (item.ArchivedAt.HasValue)
            throw new InvalidOperationException("Restore this personal task before editing it.");
        var normalized = await WorkItemMentionCodec.NormalizeAndValidateAsync(
            db, organizationId, request.Title, request.Description, request.Mentions,
            cancellationToken);
        item.Title = normalized.Title;
        item.Description = normalized.Description;
        item.StructuredMentionsJson = normalized.MentionsJson;
        item.Priority = Enum.Parse<WorkTaskPriority>(request.Priority, true);
        item.DueDate = request.DueDate;
        item.Revision++;
        item.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return await MapItemAsync(item, cancellationToken);
    }

    public async Task<Wire.PersonalTodoItem> ArchiveAsync(
        Guid organizationId, PersonalTodoActor actor, Wire.ArchivePersonalTodoItemRequest request,
        CancellationToken cancellationToken = default)
    {
        var item = await LoadPersonalItemAsync(organizationId, request.ItemId, cancellationToken);
        await RequireGrantAsync(organizationId, item.BoardId!.Value, actor,
            PersonalTodoActions.Archive, cancellationToken);
        RequireRevision(item, request.ExpectedRevision);
        if (item.ArchivedAt.HasValue)
            return await MapItemAsync(item, cancellationToken);
        item.ArchivedAt = clock.GetUtcNow();
        item.ClaimEventId = null;
        item.ClaimExpiresAt = null;
        item.Revision++;
        item.UpdatedAt = item.ArchivedAt.Value;
        await db.SaveChangesAsync(cancellationToken);
        return await MapItemAsync(item, cancellationToken);
    }

    public async Task<Wire.PersonalTodoItem> RestoreAsync(
        Guid organizationId, PersonalTodoActor actor, Wire.RestorePersonalTodoItemRequest request,
        CancellationToken cancellationToken = default)
    {
        var item = await LoadPersonalItemAsync(organizationId, request.ItemId, cancellationToken);
        await RequireGrantAsync(organizationId, item.BoardId!.Value, actor,
            PersonalTodoActions.Restore, cancellationToken);
        RequireRevision(item, request.ExpectedRevision);
        if (!item.ArchivedAt.HasValue)
            return await MapItemAsync(item, cancellationToken);
        item.ArchivedAt = null;
        item.Revision++;
        item.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return await MapItemAsync(item, cancellationToken);
    }

    public async Task<Wire.PersonalTodoItem> SetHumanStatusAsync(
        Guid organizationId, PersonalTodoActor actor, Wire.SetHumanPersonalTodoStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        if (actor.AgentInstallationId.HasValue)
            throw new UnauthorizedAccessException("Agent installations must use claim-based transitions.");
        var actorUser = await EnsureActorAsync(organizationId, actor, cancellationToken);
        if (actorUser.EmployeeType != EmployeeType.Human)
            throw new UnauthorizedAccessException("Human board transitions require a human employee owner.");
        var item = await LoadPersonalItemAsync(organizationId, request.ItemId, cancellationToken);
        if (item.Board!.OwnerOrganizationUserId != actorUser.Id)
            throw new UnauthorizedAccessException("Managers cannot impersonate an employee's execution state.");
        if (item.ArchivedAt.HasValue)
            throw new InvalidOperationException("Restore this personal task before changing its status.");
        if (!Enum.TryParse<WorkTaskStatus>(request.Status, true, out var status) ||
            status is not (WorkTaskStatus.Ready or WorkTaskStatus.Running or
                WorkTaskStatus.Blocked or WorkTaskStatus.Completed))
            throw new ArgumentException("The requested personal task status is invalid.");
        if (status == WorkTaskStatus.Blocked && string.IsNullOrWhiteSpace(request.Reason))
            throw new ArgumentException("A block reason is required.");
        RequireRevision(item, request.ExpectedRevision);
        var action = status switch
        {
            WorkTaskStatus.Completed => PersonalTodoActions.Complete,
            WorkTaskStatus.Blocked => PersonalTodoActions.Block,
            _ => PersonalTodoActions.Release
        };
        await RequireGrantAsync(organizationId, item.BoardId!.Value, actor, action, cancellationToken);
        var board = await db.WorkBoards.Include(x => x.Columns)
            .SingleAsync(x => x.Id == item.BoardId, cancellationToken);
        var now = clock.GetUtcNow();
        item.Status = status;
        item.BoardColumnId = ColumnForStatus(board, status).Id;
        item.ResultSummary = status == WorkTaskStatus.Completed ? request.Summary?.Trim() : null;
        item.BlockReason = status == WorkTaskStatus.Blocked ? request.Reason?.Trim() : null;
        item.ClaimEventId = null;
        item.ClaimExpiresAt = null;
        item.Revision++;
        item.UpdatedAt = now;
        if (status == WorkTaskStatus.Blocked)
            await AddBlockedNotificationsAsync(item, board, item.BlockReason!, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await MapItemAsync(item, cancellationToken);
    }

    public async Task<Wire.PersonalTodoClaim> ClaimAsync(
        Guid organizationId, PersonalTodoActor actor, Wire.ClaimPersonalTodoItemRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!actor.AgentInstallationId.HasValue)
            throw new UnauthorizedAccessException("Personal work may only be claimed by an agent installation.");
        var owner = await EnsureActorAsync(organizationId, actor, cancellationToken);
        if (owner.EmployeeType != EmployeeType.Agent || owner.AgentInstallationId != actor.AgentInstallationId)
            throw new UnauthorizedAccessException("An installation may claim only its own personal work.");
        await EnsureBoardAsync(organizationId, owner.Id, cancellationToken);
        var board = await PersonalBoardAsync(organizationId, owner.Id, cancellationToken);
        await RequireGrantAsync(organizationId, board.Id, actor, PersonalTodoActions.Claim, cancellationToken);
        var now = clock.GetUtcNow();
        var columns = board.Columns.ToDictionary(x => x.Category);
        var expired = await db.CoreWorkTasks.Where(x => x.BoardId == board.Id && x.Status == WorkTaskStatus.Running &&
            x.ClaimExpiresAt < now).ToListAsync(cancellationToken);
        foreach (var stale in expired)
        {
            stale.Status = WorkTaskStatus.Ready; stale.BoardColumnId = columns[WorkBoardColumnCategory.ToDo].Id;
            stale.ClaimEventId = null; stale.ClaimExpiresAt = null;
            stale.Revision++; stale.UpdatedAt = now;
        }
        if (expired.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            foreach (var stale in expired)
                db.Entry(stale).State = EntityState.Detached;
        }
        var item = await db.CoreWorkTasks.FirstOrDefaultAsync(x => x.BoardId == board.Id &&
            x.Status == WorkTaskStatus.Running && x.ClaimEventId == request.EventId,
            cancellationToken);
        while (item is null)
        {
            var candidate = await db.CoreWorkTasks.AsNoTracking()
                .Where(x => x.BoardId == board.Id && x.ArchivedAt == null &&
                    x.Status == WorkTaskStatus.Ready)
                .OrderBy(x => x.BoardRank).ThenBy(x => x.CreatedAt)
                .Select(x => new { x.Id, x.Revision })
                .FirstOrDefaultAsync(cancellationToken);
            if (candidate is null)
                break;
            var updated = await db.CoreWorkTasks
                .Where(x => x.Id == candidate.Id && x.Status == WorkTaskStatus.Ready &&
                    x.Revision == candidate.Revision)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, WorkTaskStatus.Running)
                    .SetProperty(x => x.BoardColumnId,
                        columns[WorkBoardColumnCategory.InProgress].Id)
                    .SetProperty(x => x.ClaimEventId, request.EventId)
                    .SetProperty(x => x.ClaimExpiresAt, now.Add(ClaimDuration))
                    .SetProperty(x => x.Revision, x => x.Revision + 1)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken);
            if (updated == 1)
                item = await db.CoreWorkTasks.SingleAsync(x => x.Id == candidate.Id,
                    cancellationToken);
        }
        if (item is null)
            return new Wire.PersonalTodoClaim(null);
        return new Wire.PersonalTodoClaim(await MapItemAsync(item, cancellationToken));
    }

    public Task<Wire.PersonalTodoItem> CompleteAsync(Guid organizationId, PersonalTodoActor actor,
        Wire.CompletePersonalTodoItemRequest request, CancellationToken cancellationToken = default) =>
        FinishAsync(organizationId, actor, request.ItemId, request.EventId, request.ExpectedRevision,
            WorkTaskStatus.Completed, request.Summary, null, PersonalTodoActions.Complete, cancellationToken);

    public Task<Wire.PersonalTodoItem> BlockAsync(Guid organizationId, PersonalTodoActor actor,
        Wire.BlockPersonalTodoItemRequest request, CancellationToken cancellationToken = default) =>
        FinishAsync(organizationId, actor, request.ItemId, request.EventId, request.ExpectedRevision,
            WorkTaskStatus.Blocked, null, request.Reason, PersonalTodoActions.Block, cancellationToken);

    public Task<Wire.PersonalTodoItem> ReleaseAsync(Guid organizationId, PersonalTodoActor actor,
        Wire.ReleasePersonalTodoItemRequest request, CancellationToken cancellationToken = default) =>
        FinishAsync(organizationId, actor, request.ItemId, request.EventId, request.ExpectedRevision,
            request.KeepInProgress ? WorkTaskStatus.Running : WorkTaskStatus.Ready,
            null, null, PersonalTodoActions.Release, cancellationToken);

    private async Task<Wire.PersonalTodoItem> FinishAsync(
        Guid organizationId, PersonalTodoActor actor, Guid itemId, Guid eventId, long expectedRevision,
        WorkTaskStatus status, string? summary, string? reason, string action,
        CancellationToken cancellationToken)
    {
        if (!actor.AgentInstallationId.HasValue)
            throw new UnauthorizedAccessException("Only the owning installation may transition claimed personal work.");
        var item = await LoadPersonalItemAsync(organizationId, itemId, cancellationToken);
        await RequireGrantAsync(organizationId, item.BoardId!.Value, actor, action, cancellationToken);
        if (item.Status != WorkTaskStatus.Running || item.ClaimEventId != eventId)
            throw new InvalidOperationException("The personal work item is not claimed by this event.");
        RequireRevision(item, expectedRevision);
        var board = await db.WorkBoards.Include(x => x.Columns).SingleAsync(x => x.Id == item.BoardId, cancellationToken);
        var now = clock.GetUtcNow();
        item.Status = status; item.ResultSummary = summary?.Trim();
        item.BlockReason = reason?.Trim(); item.ClaimEventId = null;
        item.ClaimExpiresAt = null; item.Revision++; item.UpdatedAt = now;
        item.BoardColumnId = ColumnForStatus(board, status).Id;
        if (status == WorkTaskStatus.Blocked)
            await AddBlockedNotificationsAsync(item, board, reason!, now, cancellationToken);
        if (status == WorkTaskStatus.Ready)
            QueueAvailable(organizationId, await OwnerAsync(board, cancellationToken), board.Id, item.Id, now);
        await db.SaveChangesAsync(cancellationToken);
        return await MapItemAsync(item, cancellationToken);
    }

    private async Task ReconcileGrantsAsync(WorkBoard board, OrganizationUser owner, CancellationToken token)
    {
        var desired = new HashSet<(GrantSubjectKind Kind, Guid Id, string Action)>();
        if (owner.EmployeeType == EmployeeType.Agent && owner.AgentInstallationId.HasValue)
        {
            foreach (var action in OwnerActions)
                desired.Add((GrantSubjectKind.AgentInstallation, owner.AgentInstallationId.Value, action));
            desired.Add((GrantSubjectKind.AgentInstallation, owner.AgentInstallationId.Value,
                WorkItemActions.Transfer));
        }
        if (owner.EmployeeType == EmployeeType.Human || owner.ApplicationUserId.HasValue)
        {
            foreach (var action in HumanOwnerActions)
                desired.Add((GrantSubjectKind.OrganizationUser, owner.Id, action));
            desired.Add((GrantSubjectKind.OrganizationUser, owner.Id, WorkItemActions.Transfer));
        }

        var organizationUsers = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == board.OrganizationId && x.IsActive)
            .Select(x => new { x.Id, x.ReportsToOrganizationUserId, x.AgentInstallationId })
            .ToListAsync(token);
        var byId = organizationUsers.ToDictionary(x => x.Id);
        var managerId = owner.ReportsToOrganizationUserId;
        var visited = new HashSet<Guid> { owner.Id };
        while (managerId.HasValue && visited.Add(managerId.Value) &&
            byId.TryGetValue(managerId.Value, out var manager))
        {
            foreach (var action in ManagerActions)
                desired.Add((GrantSubjectKind.OrganizationUser, manager.Id, action));
            if (manager.AgentInstallationId.HasValue)
                foreach (var action in ManagerActions)
                    desired.Add((GrantSubjectKind.AgentInstallation, manager.AgentInstallationId.Value, action));
            managerId = manager.ReportsToOrganizationUserId;
        }
        var active = await db.ScopedActionGrants.Where(x => x.OrganizationId == board.OrganizationId &&
            x.ScopeKind == GrantScopeKind.Board && x.ScopeId == board.Id && x.RevokedAt == null &&
            (PersonalTodoActions.All.Contains(x.Action) || x.Action == WorkItemActions.Transfer)).ToListAsync(token);
        var now = clock.GetUtcNow();
        foreach (var grant in active.Where(x => !desired.Contains((x.SubjectKind, x.SubjectId, x.Action))))
        {
            grant.RevokedAt = now; grant.Revision++;
        }
        foreach (var grant in desired.Where(x => !active.Any(y => y.SubjectKind == x.Kind && y.SubjectId == x.Id && y.Action == x.Action)))
        {
            db.ScopedActionGrants.Add(new ScopedActionGrant
            {
                Id = Guid.NewGuid(), OrganizationId = board.OrganizationId, SubjectKind = grant.Kind,
                SubjectId = grant.Id, Action = grant.Action, ScopeKind = GrantScopeKind.Board,
                ScopeId = board.Id, CanDelegate = false, GrantedBySubjectKind = GrantSubjectKind.OrganizationUser,
                GrantedBySubjectId = owner.Id, GrantedAt = now
            });
        }
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(token);
    }

    private async Task RequireGrantAsync(Guid organizationId, Guid boardId, PersonalTodoActor actor,
        string action, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var kind = actor.AgentInstallationId.HasValue ? GrantSubjectKind.AgentInstallation : GrantSubjectKind.OrganizationUser;
        var id = actor.AgentInstallationId ?? actor.OrganizationUserId;
        if (!await db.ScopedActionGrants.AsNoTracking().AnyAsync(x => x.OrganizationId == organizationId &&
            x.SubjectKind == kind && x.SubjectId == id && x.Action == action &&
            x.ScopeKind == GrantScopeKind.Board && x.ScopeId == boardId && x.RevokedAt == null &&
            (!x.ExpiresAt.HasValue || x.ExpiresAt > now), token))
            throw new UnauthorizedAccessException($"The personal to-do action '{action}' is not granted.");
    }

    private async Task<OrganizationUser> EnsureActorAsync(Guid organizationId, PersonalTodoActor actor, CancellationToken token)
    {
        var user = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.Id == actor.OrganizationUserId &&
            x.OrganizationId == organizationId && x.IsActive, token)
            ?? throw new UnauthorizedAccessException("The personal to-do actor is not active in this organization.");
        if (actor.AgentInstallationId.HasValue && user.AgentInstallationId != actor.AgentInstallationId)
            throw new UnauthorizedAccessException("The installation is not linked to the personal to-do actor.");
        return user;
    }

    private async Task<HashSet<Guid>> AccessibleOwnerIdsAsync(Guid organizationId, Guid actorId,
        CancellationToken token)
    {
        var employees = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.IsActive)
            .Select(x => new { x.Id, x.ReportsToOrganizationUserId })
            .ToListAsync(token);
        if (!employees.Any(x => x.Id == actorId))
            throw new UnauthorizedAccessException("The employee is not active in this organization.");
        var children = employees.Where(x => x.ReportsToOrganizationUserId.HasValue)
            .GroupBy(x => x.ReportsToOrganizationUserId!.Value)
            .ToDictionary(x => x.Key, x => x.Select(y => y.Id).ToArray());
        var result = new HashSet<Guid>();
        var pending = new Queue<Guid>();
        pending.Enqueue(actorId);
        while (pending.TryDequeue(out var current))
        {
            if (!result.Add(current))
                return [];
            if (children.TryGetValue(current, out var directReports))
                foreach (var child in directReports)
                    pending.Enqueue(child);
        }
        return result;
    }

    private Task<WorkBoard> PersonalBoardAsync(Guid organizationId, Guid ownerId, CancellationToken token) =>
        db.WorkBoards.Include(x => x.Columns).SingleAsync(x => x.OrganizationId == organizationId &&
            x.Kind == WorkBoardKind.Personal && x.OwnerOrganizationUserId == ownerId, token);

    private async Task<WorkTask> LoadPersonalItemAsync(Guid organizationId, Guid itemId, CancellationToken token) =>
        await db.CoreWorkTasks.Include(x => x.Board).SingleOrDefaultAsync(x => x.Id == itemId &&
            x.OrganizationId == organizationId && x.Board != null && x.Board.Kind == WorkBoardKind.Personal, token)
        ?? throw new KeyNotFoundException("The personal work item was not found.");

    private Task<OrganizationUser> OwnerAsync(WorkBoard board, CancellationToken token) =>
        db.CoreOrganizationUsers.SingleAsync(x => x.Id == board.OwnerOrganizationUserId, token);

    private async Task ValidateSourceAsync(Guid organizationId, Guid ownerId,
        Wire.AddPersonalTodoItemRequest request, CancellationToken token)
    {
        if (!request.SourceMessageId.HasValue || !request.SourceConversationId.HasValue)
            throw new ArgumentException("Source conversation and message IDs must be supplied together.");
        if (!await db.ChatTurns.AsNoTracking().AnyAsync(x => x.OrganizationId == organizationId &&
            x.ConversationId == request.SourceConversationId && x.UserMessageId == request.SourceMessageId &&
            x.TargetAgentOrganizationUserId == ownerId, token))
            throw new UnauthorizedAccessException("The source message was not addressed to the target agent.");
    }

    private static void ValidateAdd(Wire.AddPersonalTodoItemRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 512)
            throw new ArgumentException("A personal to-do title between 1 and 512 characters is required.");
        if ((request.Description?.Trim().Length ?? 0) > 8192)
            throw new ArgumentException("A personal to-do description cannot exceed 8192 characters.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Trim().Length > 160)
            throw new ArgumentException("A personal to-do idempotency key is required.");
        if (!Enum.TryParse<WorkTaskPriority>(request.Priority, true, out _))
            throw new ArgumentException("The personal to-do priority is invalid.");
        if ((request.CorrelationId?.Trim().Length ?? 0) > 160 ||
            (request.CausationId?.Trim().Length ?? 0) > 160)
            throw new ArgumentException("Correlation and causation IDs cannot exceed 160 characters.");
    }

    private static void ValidateEditableFields(string title, string? description, string priority)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 512)
            throw new ArgumentException("A personal task title between 1 and 512 characters is required.");
        if ((description?.Trim().Length ?? 0) > 8192)
            throw new ArgumentException("A personal task description cannot exceed 8192 characters.");
        if (!Enum.TryParse<WorkTaskPriority>(priority, true, out _))
            throw new ArgumentException("The personal task priority is invalid.");
    }

    private void QueueAvailable(Guid organizationId, OrganizationUser owner, Guid boardId, Guid itemId, DateTimeOffset now)
    {
        if (!owner.AgentInstallationId.HasValue)
            return;
        var eventId = Guid.NewGuid();
        db.AgentPlatformEventOutbox.Add(new AgentPlatformEventOutboxItem
        {
            Id = eventId, OrganizationId = organizationId, TargetInstallationId = owner.AgentInstallationId,
            EventType = Wire.PersonalTodoEvents.Available,
            DataJson = JsonSerializer.Serialize(new Wire.PersonalTodoAvailableEvent(owner.Id, boardId, itemId)),
            IdempotencyKey = $"personal-todo-available:{itemId:N}:{eventId:N}",
            Status = AgentPlatformEventOutboxStatus.Pending, NextAttemptAt = now, OccurredAt = now
        });
    }

    private async Task AddManagerCreatedNotificationAsync(OrganizationUser actor,
        OrganizationUser owner, WorkTask item, CancellationToken token)
    {
        if (actor.Id == owner.Id || owner.EmployeeType != EmployeeType.Human)
            return;
        if (await db.UserNotifications.AsNoTracking().AnyAsync(x =>
            x.OrganizationId == item.OrganizationId &&
            x.RecipientOrganizationUserId == owner.Id &&
            x.DeduplicationKey == $"personal-task-added:{item.Id:N}", token))
            return;
        db.UserNotifications.Add(new UserNotification
        {
            Id = Guid.NewGuid(), OrganizationId = item.OrganizationId,
            RecipientOrganizationUserId = owner.Id, Severity = NotificationSeverity.Routine,
            Category = "PersonalTaskAdded", Title = $"{actor.DisplayName} added personal work",
            Body = item.Title, ActionUri = $"/organizations/{item.OrganizationId:D}/employees/{owner.Id:D}",
            DeduplicationKey = $"personal-task-added:{item.Id:N}", CreatedAt = item.CreatedAt
        });
    }

    private async Task AddOpenLimitNotificationsAsync(OrganizationUser owner, int openCount,
        CancellationToken token)
    {
        var recipientIds = new HashSet<Guid>();
        if (owner.EmployeeType == EmployeeType.Human)
            recipientIds.Add(owner.Id);
        var managerId = owner.ReportsToOrganizationUserId;
        var visited = new HashSet<Guid>();
        while (managerId.HasValue && visited.Add(managerId.Value))
        {
            var manager = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == managerId.Value && x.OrganizationId == owner.OrganizationId && x.IsActive, token);
            if (manager is null) break;
            if (manager.EmployeeType == EmployeeType.Human) recipientIds.Add(manager.Id);
            managerId = manager.ReportsToOrganizationUserId;
        }
        var thresholdBucket = openCount / 25;
        foreach (var recipientId in recipientIds)
        {
            var deduplicationKey = $"personal-task-soft-limit:{owner.Id:N}:{thresholdBucket}";
            if (await db.UserNotifications.AsNoTracking().AnyAsync(x =>
                x.OrganizationId == owner.OrganizationId &&
                x.RecipientOrganizationUserId == recipientId &&
                x.DeduplicationKey == deduplicationKey, token))
                continue;
            db.UserNotifications.Add(new UserNotification
            {
                Id = Guid.NewGuid(), OrganizationId = owner.OrganizationId,
                RecipientOrganizationUserId = recipientId, Severity = NotificationSeverity.Important,
                Category = "PersonalTaskLimit", Title = $"{owner.DisplayName}'s personal board is growing",
                Body = $"The board now has {openCount} open tasks. The creation limit is {HardOpenItemLimit}.",
                ActionUri = $"/organizations/{owner.OrganizationId:D}/employees/{owner.Id:D}",
                DeduplicationKey = deduplicationKey, CreatedAt = clock.GetUtcNow()
            });
        }
    }

    private async Task AddBlockedNotificationsAsync(WorkTask item, WorkBoard board, string reason,
        DateTimeOffset now, CancellationToken token)
    {
        var recipients = new HashSet<Guid>();
        if (item.CreatedByOrganizationUserId.HasValue) recipients.Add(item.CreatedByOrganizationUserId.Value);
        if (board.ManagerOrganizationUserId.HasValue) recipients.Add(board.ManagerOrganizationUserId.Value);
        var humans = await db.CoreOrganizationUsers.AsNoTracking().Where(x => recipients.Contains(x.Id) &&
            x.IsActive && x.EmployeeType == EmployeeType.Human).Select(x => x.Id).ToListAsync(token);
        foreach (var recipient in humans)
        {
            db.UserNotifications.Add(new UserNotification
            {
                Id = Guid.NewGuid(), OrganizationId = item.OrganizationId,
                RecipientOrganizationUserId = recipient, Severity = NotificationSeverity.Important,
                Category = "PersonalTodoBlocked", Title = "Personal task blocked",
                Body = $"{NotificationExcerpt(item.Title, BlockedNotificationTitleExcerptLength)} — " +
                    NotificationExcerpt(reason, BlockedNotificationReasonExcerptLength),
                ActionUri = $"/organizations/{item.OrganizationId:D}/employees/{board.OwnerOrganizationUserId!.Value:D}",
                DeduplicationKey = $"personal-todo-blocked:{item.Id:N}:{item.Revision + 1}", CreatedAt = now
            });
        }
    }

    private static string NotificationExcerpt(string value, int maximumLength)
    {
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maximumLength
            ? normalized
            : $"{normalized[..(maximumLength - 1)].TrimEnd()}…";
    }

    private async Task<Wire.PersonalTodoBoard> MapBoardAsync(WorkBoard board, bool includeArchived,
        CancellationToken token)
    {
        var ownerId = board.OwnerOrganizationUserId!.Value;
        var names = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.Id == ownerId || x.Id == board.ManagerOrganizationUserId)
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, token);
        var items = await db.CoreWorkTasks.AsNoTracking().Where(x =>
                x.BoardId == board.Id && (includeArchived || x.ArchivedAt == null))
            .OrderBy(x => x.BoardRank).ToListAsync(token);
        var mapped = new List<Wire.PersonalTodoItem>();
        foreach (var item in items) mapped.Add(await MapItemAsync(item, token));
        return new Wire.PersonalTodoBoard(board.Id, ownerId, names[ownerId], board.ManagerOrganizationUserId,
            board.ManagerOrganizationUserId.HasValue && names.TryGetValue(board.ManagerOrganizationUserId.Value, out var manager)
                ? manager : null, board.Revision, mapped);
    }

    private async Task<Wire.PersonalTodoItem> MapItemAsync(WorkTask item, CancellationToken token)
    {
        var creator = item.CreatedByOrganizationUserId.HasValue
            ? await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.Id == item.CreatedByOrganizationUserId)
                .Select(x => x.DisplayName).SingleOrDefaultAsync(token)
            : null;
        var sourceMentions = item.SourceMessageId.HasValue
            ? await db.ConversationMessageMentions.AsNoTracking()
                .Where(x => x.MessageId == item.SourceMessageId)
                .Include(x => x.MentionedOrganizationUser)
                .OrderBy(x => x.Offset)
                .Select(x => new Wire.PersonalTodoMention(x.MentionedOrganizationUserId,
                    x.MentionedOrganizationUser!.DisplayName, x.MentionedOrganizationUser.EmployeeType.ToString()))
                .ToListAsync(token)
            : [];
        var mentionSpans = WorkItemMentionCodec.Deserialize(item.StructuredMentionsJson);
        var mentions = sourceMentions.Concat(mentionSpans.Select(x =>
                new Wire.PersonalTodoMention(x.OrganizationUserId, x.DisplayName, x.EmployeeType)))
            .GroupBy(x => x.OrganizationUserId)
            .Select(x => x.First())
            .ToList();
        return new Wire.PersonalTodoItem(item.Id, item.BoardId!.Value, item.AssignedEmployeeId!.Value,
            item.CreatedByOrganizationUserId ?? item.AssignedEmployeeId.Value, creator ?? "Unknown",
            item.Title, item.Description, item.Status.ToString(), item.Priority.ToString(), item.BoardRank,
            item.Revision, item.DueDate, item.SourceConversationId, item.SourceMessageId, mentions,
            item.ResultSummary, item.BlockReason, item.CreatedAt, item.UpdatedAt, item.ArchivedAt)
        {
            MentionSpans = mentionSpans,
            CorrelationId = item.CorrelationId
        };
    }

    private static WorkBoardColumn NewColumn(string name, WorkBoardColumnCategory category, int position) =>
        new() { Id = Guid.NewGuid(), Name = name, Category = category, Position = position,
            WipPolicy = WorkBoardWipPolicy.Disabled };

    private static WorkBoardColumn ColumnForStatus(WorkBoard board, WorkTaskStatus status) => status switch
    {
        WorkTaskStatus.Completed => board.Columns.Single(x => x.Category == WorkBoardColumnCategory.Done),
        WorkTaskStatus.Ready => board.Columns.Single(x => x.Category == WorkBoardColumnCategory.ToDo),
        WorkTaskStatus.Blocked => board.Columns.Single(x => x.Category == WorkBoardColumnCategory.Blocked),
        WorkTaskStatus.Running => board.Columns.Single(x => x.Category == WorkBoardColumnCategory.InProgress),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported personal task status.")
    };

    private static string PersonalKey(Guid ownerId) => $"TD{ownerId:N}"[..12].ToUpperInvariant();

    private static string PersonalName(string displayName) =>
        $"{displayName[..Math.Min(displayName.Length, 152)]}'s To Do";

    private static void RequireRevision(WorkTask item, long expected)
    {
        if (item.Revision != expected) throw new DbUpdateConcurrencyException("The personal work item changed since it was loaded.");
    }
}
