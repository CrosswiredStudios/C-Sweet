using System.Text.Json;
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

public sealed class PersonalTodoService(CSweetDbContext db, TimeProvider clock) : IPersonalTodoService
{
    private static readonly TimeSpan ClaimDuration = TimeSpan.FromMinutes(5);
    private static readonly IReadOnlySet<string> OwnerActions = new HashSet<string>(
        [PersonalTodoActions.Read, PersonalTodoActions.Add, PersonalTodoActions.Requeue,
         PersonalTodoActions.Claim, PersonalTodoActions.Complete, PersonalTodoActions.Block,
         PersonalTodoActions.Release], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> ManagerActions = new HashSet<string>(
        [PersonalTodoActions.Read, PersonalTodoActions.Add, PersonalTodoActions.Reorder,
         PersonalTodoActions.Requeue], StringComparer.Ordinal);

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var activeOwners = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.IsActive && x.EmployeeType == EmployeeType.Agent &&
                x.AgentInstallationId != null)
            .Select(x => new { x.OrganizationId, x.Id })
            .ToListAsync(cancellationToken);
        foreach (var owner in activeOwners)
            await EnsureBoardAsync(owner.OrganizationId, owner.Id, cancellationToken);

        var activeOwnerIds = activeOwners.Select(x => x.Id).ToHashSet();
        var inactiveBoardIds = await db.WorkBoards.AsNoTracking()
            .Where(x => x.IsPersonalTodo &&
                (!x.PersonalTodoOwnerOrganizationUserId.HasValue ||
                 !activeOwnerIds.Contains(x.PersonalTodoOwnerOrganizationUserId.Value)))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var now = clock.GetUtcNow();
        if (inactiveBoardIds.Count > 0)
        {
            var inactiveGrants = await db.ScopedActionGrants.Where(x =>
                x.ScopeKind == GrantScopeKind.Board && x.ScopeId.HasValue &&
                inactiveBoardIds.Contains(x.ScopeId.Value) && x.RevokedAt == null &&
                PersonalTodoActions.All.Contains(x.Action)).ToListAsync(cancellationToken);
            foreach (var grant in inactiveGrants)
            {
                grant.RevokedAt = now;
                grant.Revision++;
            }
        }

        var expired = await db.CoreWorkTasks
            .Include(x => x.Board)
            .Where(x => x.Board != null && x.Board.IsPersonalTodo &&
                x.Status == WorkTaskStatus.Running && x.PersonalTodoClaimExpiresAt < now)
            .ToListAsync(cancellationToken);
        foreach (var item in expired)
        {
            var owner = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
                x.Id == item.Board!.PersonalTodoOwnerOrganizationUserId && x.IsActive &&
                x.AgentInstallationId != null, cancellationToken);
            if (owner is null)
                continue;
            var todoColumnId = await db.WorkBoardColumns.AsNoTracking()
                .Where(x => x.BoardId == item.BoardId && x.Category == WorkBoardColumnCategory.ToDo)
                .Select(x => x.Id).SingleAsync(cancellationToken);
            item.Status = WorkTaskStatus.Ready;
            item.BoardColumnId = todoColumnId;
            item.PersonalTodoClaimEventId = null;
            item.PersonalTodoClaimExpiresAt = null;
            item.Revision++;
            item.UpdatedAt = now;
            QueueAvailable(item.OrganizationId, owner, item.BoardId!.Value, item.Id, now);
        }
        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(cancellationToken);
    }

    public async Task EnsureBoardAsync(Guid organizationId, Guid agentOrganizationUserId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var owner = await db.CoreOrganizationUsers
            .SingleOrDefaultAsync(x => x.Id == agentOrganizationUserId &&
                x.OrganizationId == organizationId && x.IsActive &&
                x.EmployeeType == EmployeeType.Agent && x.AgentInstallationId != null,
                cancellationToken)
            ?? throw new ArgumentException("The personal to-do owner must be an active linked agent.");
        var board = await db.WorkBoards.Include(x => x.Columns)
            .SingleOrDefaultAsync(x => x.PersonalTodoOwnerOrganizationUserId == owner.Id,
                cancellationToken);
        var now = clock.GetUtcNow();
        if (board is null)
        {
            board = new WorkBoard
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId,
                PersonalTodoOwnerOrganizationUserId = owner.Id, IsPersonalTodo = true,
                ManagerOrganizationUserId = owner.ReportsToOrganizationUserId,
                Key = PersonalKey(owner.Id), Name = PersonalName(owner.DisplayName),
                Description = "Protected personal work queue.", CreatedAt = now, UpdatedAt = now,
                Columns =
                [
                    NewColumn("To Do", WorkBoardColumnCategory.ToDo, 0),
                    NewColumn("Doing", WorkBoardColumnCategory.InProgress, 1),
                    NewColumn("Done", WorkBoardColumnCategory.Done, 2)
                ]
            };
            db.WorkBoards.Add(board);
        }
        else
        {
            board.IsPersonalTodo = true;
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
        Guid organizationId, PersonalTodoActor actor, CancellationToken cancellationToken = default)
    {
        await EnsureActorAsync(organizationId, actor, cancellationToken);
        var owners = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.IsActive && x.EmployeeType == EmployeeType.Agent &&
                (x.Id == actor.OrganizationUserId || x.ReportsToOrganizationUserId == actor.OrganizationUserId))
            .Select(x => x.Id).ToListAsync(cancellationToken);
        foreach (var ownerId in owners)
            await EnsureBoardAsync(organizationId, ownerId, cancellationToken);

        var boards = await db.WorkBoards.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.IsPersonalTodo &&
                x.PersonalTodoOwnerOrganizationUserId.HasValue && owners.Contains(x.PersonalTodoOwnerOrganizationUserId.Value))
            .Include(x => x.Columns)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
        var results = new List<Wire.PersonalTodoBoard>();
        foreach (var board in boards)
        {
            await RequireGrantAsync(organizationId, board.Id, actor, PersonalTodoActions.Read, cancellationToken);
            results.Add(await MapBoardAsync(board, cancellationToken));
        }
        return new Wire.PersonalTodoDirectory(results);
    }

    public async Task<Wire.PersonalTodoItem> AddAsync(
        Guid organizationId, PersonalTodoActor actor, Wire.AddPersonalTodoItemRequest request,
        CancellationToken cancellationToken = default)
    {
        var actorUser = await EnsureActorAsync(organizationId, actor, cancellationToken);
        var ownerId = request.TargetOrganizationUserId ?? actor.OrganizationUserId;
        var owner = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
            x.Id == ownerId && x.OrganizationId == organizationId && x.IsActive &&
            x.EmployeeType == EmployeeType.Agent && x.AgentInstallationId != null, cancellationToken)
            ?? throw new ArgumentException("The personal to-do target is not an active linked agent.");
        if (owner.Id != actor.OrganizationUserId && owner.ReportsToOrganizationUserId != actor.OrganizationUserId)
            throw new UnauthorizedAccessException("Only an agent or its direct manager may add personal work.");
        await EnsureBoardAsync(organizationId, owner.Id, cancellationToken);
        var board = await PersonalBoardAsync(organizationId, owner.Id, cancellationToken);
        await RequireGrantAsync(organizationId, board.Id, actor, PersonalTodoActions.Add, cancellationToken);
        ValidateAdd(request);

        var key = request.IdempotencyKey.Trim();
        var existing = await db.CoreWorkTasks.AsNoTracking().SingleOrDefaultAsync(x =>
            x.CreatedByOrganizationUserId == actor.OrganizationUserId &&
            x.PersonalTodoIdempotencyKey == key, cancellationToken);
        if (existing is not null) return await MapItemAsync(existing, cancellationToken);

        if (request.SourceMessageId.HasValue || request.SourceConversationId.HasValue)
            await ValidateSourceAsync(organizationId, owner.Id, request, cancellationToken);
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
            PersonalTodoIdempotencyKey = key,
            Title = request.Title.Trim(), Description = request.Description?.Trim() ?? string.Empty,
            Kind = WorkItemKind.Task, Status = WorkTaskStatus.Ready,
            Priority = Enum.Parse<WorkTaskPriority>(request.Priority, true),
            DueDate = request.DueDate,
            BoardRank = (await db.CoreWorkTasks.Where(x => x.BoardId == board.Id)
                .Select(x => (long?)x.BoardRank).MaxAsync(cancellationToken) ?? 0) + 1024,
            CreatedAt = now, UpdatedAt = now
        };
        db.CoreWorkTasks.Add(item);
        QueueAvailable(organizationId, owner, board.Id, item.Id, now);
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
        var ready = await db.CoreWorkTasks.Where(x => x.BoardId == item.BoardId && x.Status == WorkTaskStatus.Ready && x.Id != item.Id)
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
        if (item.Status != WorkTaskStatus.Blocked)
            throw new InvalidOperationException("Only blocked personal work can be requeued.");
        RequireRevision(item, request.ExpectedRevision);
        var board = await db.WorkBoards.Include(x => x.Columns).SingleAsync(x => x.Id == item.BoardId, cancellationToken);
        item.Status = WorkTaskStatus.Ready;
        item.BoardColumnId = board.Columns.Single(x => x.Category == WorkBoardColumnCategory.ToDo).Id;
        item.PersonalTodoBlockReason = null; item.PersonalTodoClaimEventId = null;
        item.PersonalTodoClaimExpiresAt = null; item.Revision++; item.UpdatedAt = clock.GetUtcNow();
        var owner = await OwnerAsync(board, cancellationToken);
        QueueAvailable(organizationId, owner, board.Id, item.Id, item.UpdatedAt);
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
            x.PersonalTodoClaimExpiresAt < now).ToListAsync(cancellationToken);
        foreach (var stale in expired)
        {
            stale.Status = WorkTaskStatus.Ready; stale.BoardColumnId = columns[WorkBoardColumnCategory.ToDo].Id;
            stale.PersonalTodoClaimEventId = null; stale.PersonalTodoClaimExpiresAt = null;
            stale.Revision++; stale.UpdatedAt = now;
        }
        if (expired.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            foreach (var stale in expired)
                db.Entry(stale).State = EntityState.Detached;
        }
        var item = await db.CoreWorkTasks.FirstOrDefaultAsync(x => x.BoardId == board.Id &&
            x.Status == WorkTaskStatus.Running && x.PersonalTodoClaimEventId == request.EventId,
            cancellationToken);
        while (item is null)
        {
            var candidate = await db.CoreWorkTasks.AsNoTracking()
                .Where(x => x.BoardId == board.Id && x.Status == WorkTaskStatus.Ready)
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
                    .SetProperty(x => x.PersonalTodoClaimEventId, request.EventId)
                    .SetProperty(x => x.PersonalTodoClaimExpiresAt, now.Add(ClaimDuration))
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
            WorkTaskStatus.Ready, null, null, PersonalTodoActions.Release, cancellationToken);

    private async Task<Wire.PersonalTodoItem> FinishAsync(
        Guid organizationId, PersonalTodoActor actor, Guid itemId, Guid eventId, long expectedRevision,
        WorkTaskStatus status, string? summary, string? reason, string action,
        CancellationToken cancellationToken)
    {
        if (!actor.AgentInstallationId.HasValue)
            throw new UnauthorizedAccessException("Only the owning installation may transition claimed personal work.");
        var item = await LoadPersonalItemAsync(organizationId, itemId, cancellationToken);
        await RequireGrantAsync(organizationId, item.BoardId!.Value, actor, action, cancellationToken);
        if (item.Status != WorkTaskStatus.Running || item.PersonalTodoClaimEventId != eventId)
            throw new InvalidOperationException("The personal work item is not claimed by this event.");
        RequireRevision(item, expectedRevision);
        var board = await db.WorkBoards.Include(x => x.Columns).SingleAsync(x => x.Id == item.BoardId, cancellationToken);
        var now = clock.GetUtcNow();
        item.Status = status; item.PersonalTodoResultSummary = summary?.Trim();
        item.PersonalTodoBlockReason = reason?.Trim(); item.PersonalTodoClaimEventId = null;
        item.PersonalTodoClaimExpiresAt = null; item.Revision++; item.UpdatedAt = now;
        item.BoardColumnId = status switch
        {
            WorkTaskStatus.Completed => board.Columns.Single(x => x.Category == WorkBoardColumnCategory.Done).Id,
            WorkTaskStatus.Ready => board.Columns.Single(x => x.Category == WorkBoardColumnCategory.ToDo).Id,
            _ => board.Columns.Single(x => x.Category == WorkBoardColumnCategory.InProgress).Id
        };
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
        foreach (var action in OwnerActions)
            desired.Add((GrantSubjectKind.AgentInstallation, owner.AgentInstallationId!.Value, action));
        if (owner.ReportsToOrganizationUserId.HasValue)
        {
            foreach (var action in ManagerActions)
                desired.Add((GrantSubjectKind.OrganizationUser, owner.ReportsToOrganizationUserId.Value, action));
            var managerInstallation = await db.CoreOrganizationUsers.AsNoTracking()
                .Where(x => x.Id == owner.ReportsToOrganizationUserId && x.IsActive)
                .Select(x => x.AgentInstallationId).SingleOrDefaultAsync(token);
            if (managerInstallation.HasValue)
                foreach (var action in ManagerActions)
                    desired.Add((GrantSubjectKind.AgentInstallation, managerInstallation.Value, action));
        }
        var active = await db.ScopedActionGrants.Where(x => x.OrganizationId == board.OrganizationId &&
            x.ScopeKind == GrantScopeKind.Board && x.ScopeId == board.Id && x.RevokedAt == null &&
            PersonalTodoActions.All.Contains(x.Action)).ToListAsync(token);
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

    private Task<WorkBoard> PersonalBoardAsync(Guid organizationId, Guid ownerId, CancellationToken token) =>
        db.WorkBoards.Include(x => x.Columns).SingleAsync(x => x.OrganizationId == organizationId &&
            x.IsPersonalTodo && x.PersonalTodoOwnerOrganizationUserId == ownerId, token);

    private async Task<WorkTask> LoadPersonalItemAsync(Guid organizationId, Guid itemId, CancellationToken token) =>
        await db.CoreWorkTasks.Include(x => x.Board).SingleOrDefaultAsync(x => x.Id == itemId &&
            x.OrganizationId == organizationId && x.Board != null && x.Board.IsPersonalTodo, token)
        ?? throw new KeyNotFoundException("The personal work item was not found.");

    private Task<OrganizationUser> OwnerAsync(WorkBoard board, CancellationToken token) =>
        db.CoreOrganizationUsers.SingleAsync(x => x.Id == board.PersonalTodoOwnerOrganizationUserId, token);

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
    }

    private void QueueAvailable(Guid organizationId, OrganizationUser owner, Guid boardId, Guid itemId, DateTimeOffset now)
    {
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
                Category = "PersonalTodoBlocked", Title = $"Personal task blocked: {item.Title}",
                Body = reason.Length <= 500 ? reason : reason[..500],
                ActionUri = $"/organizations/{item.OrganizationId:D}/work/boards/{board.Id:D}",
                DeduplicationKey = $"personal-todo-blocked:{item.Id:N}:{item.Revision + 1}", CreatedAt = now
            });
        }
    }

    private async Task<Wire.PersonalTodoBoard> MapBoardAsync(WorkBoard board, CancellationToken token)
    {
        var ownerId = board.PersonalTodoOwnerOrganizationUserId!.Value;
        var names = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.Id == ownerId || x.Id == board.ManagerOrganizationUserId)
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, token);
        var items = await db.CoreWorkTasks.AsNoTracking().Where(x => x.BoardId == board.Id)
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
        var mentions = item.SourceMessageId.HasValue
            ? await db.ConversationMessageMentions.AsNoTracking()
                .Where(x => x.MessageId == item.SourceMessageId)
                .Include(x => x.MentionedOrganizationUser)
                .OrderBy(x => x.Offset)
                .Select(x => new Wire.PersonalTodoMention(x.MentionedOrganizationUserId,
                    x.MentionedOrganizationUser!.DisplayName, x.MentionedOrganizationUser.EmployeeType.ToString()))
                .ToListAsync(token)
            : [];
        return new Wire.PersonalTodoItem(item.Id, item.BoardId!.Value, item.AssignedEmployeeId!.Value,
            item.CreatedByOrganizationUserId ?? item.AssignedEmployeeId.Value, creator ?? "Unknown",
            item.Title, item.Description, item.Status.ToString(), item.Priority.ToString(), item.BoardRank,
            item.Revision, item.DueDate, item.SourceConversationId, item.SourceMessageId, mentions,
            item.PersonalTodoResultSummary, item.PersonalTodoBlockReason, item.CreatedAt, item.UpdatedAt);
    }

    private static WorkBoardColumn NewColumn(string name, WorkBoardColumnCategory category, int position) =>
        new() { Id = Guid.NewGuid(), Name = name, Category = category, Position = position,
            WipPolicy = WorkBoardWipPolicy.Disabled };

    private static string PersonalKey(Guid ownerId) => $"TD{ownerId:N}"[..12].ToUpperInvariant();

    private static string PersonalName(string displayName) =>
        $"{displayName[..Math.Min(displayName.Length, 152)]}'s To Do";

    private static void RequireRevision(WorkTask item, long expected)
    {
        if (item.Revision != expected) throw new DbUpdateConcurrencyException("The personal work item changed since it was loaded.");
    }
}
