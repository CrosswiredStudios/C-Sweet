using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Security;
using CSweet.Application.Setup;
using CSweet.Contracts.Realtime;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Notifications;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.AgentHost.Broker;

public sealed class WorkManagementCapabilityHandler(
    CSweetDbContext db,
    IScopedActionAuthorizationService authorization,
    IAuditEventWriter audit) : IPlatformCapabilityHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> HandledCapabilities =
    [
        WorkBoardActions.Read,
        WorkBoardActions.Create,
        WorkItemActions.Read,
        WorkItemActions.Create,
        WorkItemActions.Comment,
        WorkItemActions.Estimate,
        WorkItemActions.Move,
        WorkItemActions.Complete,
        WorkItemActions.Cancel,
        WorkItemActions.Reopen,
        WorkItemActions.Transfer,
        WorkSprintActions.Read,
        WorkSprintActions.Create,
        WorkSprintActions.Start,
        WorkSprintActions.Complete,
        WorkSprintActions.Cancel,
        WorkSprintActions.ManageScope,
        WorkSprintActions.ManageCapacity,
        WorkSprintActions.CarryOver,
        WorkSprintActions.ReadReports,
        WorkAutomationActions.Read,
        WorkAutomationActions.Manage
    ];

    public bool CanHandle(string capability) => HandledCapabilities.Contains(capability);

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(
        AgentSession session,
        RequestCapability request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return await HandleCoreAsync(session, request, cancellationToken);
    }

    private async Task<CapabilityResult> HandleCoreAsync(
        AgentSession session,
        RequestCapability request,
        CancellationToken cancellationToken)
    {
        if (!session.Grant.RequestedCapabilities.Contains(request.Capability))
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied,
                $"The installation capability grant does not include '{request.Capability}'.");
        if (!Guid.TryParse(session.BusinessId, out var organizationId) ||
            !Guid.TryParse(session.InstallationId, out var installationId))
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied,
                "The agent organization or installation identity is invalid.");
        var installation = await db.AgentInstallations.AsNoTracking()
            .SingleOrDefaultAsync(x =>
                x.Id == installationId &&
                x.BusinessId == session.BusinessId &&
                x.IsEnabled &&
                x.RevisionStatus == PluginRevisionStatus.Active,
                cancellationToken);
        if (installation is null)
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied,
                "The agent installation is not active in this organization.");

        try
        {
            return request.Capability switch
            {
                WorkBoardActions.Read => Success(
                    request.RequestId,
                    await ListBoardsAsync(
                        organizationId, installationId,
                        Read<Wire.WorkBoardListRequest>(request), cancellationToken)),
                WorkItemActions.Read => Success(
                    request.RequestId,
                    await ReadBoardAsync(
                        organizationId, installationId,
                        Read<Wire.WorkBoardReference>(request), cancellationToken)),
                WorkBoardActions.Create => Success(
                    request.RequestId,
                    await CreateBoardAsync(
                        session, organizationId, installation,
                        Read<Wire.CreateWorkBoardRequest>(request), cancellationToken)),
                WorkItemActions.Create => Success(
                    request.RequestId,
                    await CreateItemAsync(
                        session, organizationId, installation,
                        Read<Wire.CreateWorkItemRequest>(request), cancellationToken)),
                WorkItemActions.Comment => Success(
                    request.RequestId,
                    await CommentItemAsync(
                        session, organizationId, installation,
                        Read<Wire.CommentOnWorkItemRequest>(request), cancellationToken)),
                WorkItemActions.Estimate => Success(
                    request.RequestId,
                    await EstimateItemAsync(
                        session, organizationId, installation,
                        Read<Wire.EstimateWorkItemRequest>(request), cancellationToken)),
                WorkItemActions.Transfer => Success(
                    request.RequestId,
                    await TransferItemAsync(
                        session, organizationId, installation,
                        Read<Wire.TransferWorkItemRequest>(request), cancellationToken)),
                WorkSprintActions.Read => Success(
                    request.RequestId,
                    await ListSprintsAsync(
                        organizationId, installation.Id,
                        Read<Wire.WorkBoardReference>(request), cancellationToken)),
                WorkSprintActions.Create => Success(
                    request.RequestId,
                    await CreateSprintAsync(
                        session, organizationId, installation,
                        Read<Wire.CreateWorkSprintRequest>(request), cancellationToken)),
                WorkSprintActions.Start or
                WorkSprintActions.Complete or
                WorkSprintActions.Cancel => Success(
                    request.RequestId,
                    await ChangeSprintStateAsync(
                        session, organizationId, installation, request.Capability,
                        Read<Wire.ChangeWorkSprintStateRequest>(request), cancellationToken)),
                WorkSprintActions.ManageScope => Success(
                    request.RequestId,
                    await SetItemSprintAsync(
                        session, organizationId, installation,
                        Read<Wire.SetWorkItemSprintRequest>(request), cancellationToken)),
                WorkSprintActions.ManageCapacity => Success(
                    request.RequestId,
                    await SetSprintCapacityAsync(
                        session, organizationId, installation,
                        Read<Wire.SetWorkSprintCapacityRequest>(request), cancellationToken)),
                WorkSprintActions.CarryOver => Success(
                    request.RequestId,
                    await CarryOverSprintAsync(
                        session, organizationId, installation,
                        Read<Wire.CarryOverWorkSprintRequest>(request), cancellationToken)),
                WorkSprintActions.ReadReports => Success(
                    request.RequestId,
                    await ReadSprintReportAsync(
                        organizationId, installation.Id,
                        Read<Wire.WorkBoardReference>(request), cancellationToken)),
                WorkAutomationActions.Read => Success(
                    request.RequestId,
                    await ReadAutomationsAsync(
                        organizationId, installation.Id,
                        Read<Wire.WorkBoardReference>(request), cancellationToken)),
                WorkAutomationActions.Manage => Success(
                    request.RequestId,
                    await ManageAutomationAsync(
                        session, organizationId, installation,
                        Read<Wire.ManageWorkAutomationRequest>(request), cancellationToken)),
                WorkItemActions.Move => Success(
                    request.RequestId,
                    await MoveItemAsync(
                        session, organizationId, installation,
                        Read<Wire.MoveWorkItemRequest>(request), cancellationToken)),
                WorkItemActions.Complete or
                WorkItemActions.Cancel or
                WorkItemActions.Reopen => Success(
                    request.RequestId,
                    await TransitionItemAsync(
                        session, organizationId, installation, request.Capability,
                        Read<Wire.TransitionWorkItemRequest>(request), cancellationToken)),
                _ => Failure(request.RequestId, PlatformCapabilityErrorCode.NotFound,
                    "The work-management capability is not implemented.")
            };
        }
        catch (JsonException)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed,
                "The capability payload is not valid JSON.");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied, exception.Message);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Conflict, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Conflict, exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed, exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.NotFound, exception.Message);
        }
    }

    private Task<Wire.WorkItem> MoveItemAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        Wire.MoveWorkItemRequest input,
        CancellationToken cancellationToken) =>
        TransitionItemAsync(
            session,
            organizationId,
            installation,
            WorkItemActions.Move,
            new Wire.TransitionWorkItemRequest(
                input.BoardId,
                input.ItemId,
                input.ExpectedRevision,
                input.IdempotencyKey,
                input.TargetColumnId),
            cancellationToken);

    private async Task<IReadOnlyList<Wire.WorkBoardSummary>> ListBoardsAsync(
        Guid organizationId,
        Guid installationId,
        Wire.WorkBoardListRequest input,
        CancellationToken cancellationToken)
    {
        var grants = await ActiveGrantsAsync(organizationId, installationId, cancellationToken);
        var organizationRead = grants.Any(x =>
            x.Action == WorkBoardActions.Read && x.ScopeKind == GrantScopeKind.Organization);
        var boardIds = grants.Where(x =>
                x.Action == WorkBoardActions.Read &&
                x.ScopeKind == GrantScopeKind.Board &&
                x.ScopeId.HasValue)
            .Select(x => x.ScopeId!.Value)
            .ToHashSet();
        if (!organizationRead && boardIds.Count == 0)
            throw new UnauthorizedAccessException("The installation has no board read grant.");

        var query = db.WorkBoards.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .Where(x => organizationRead || boardIds.Contains(x.Id));
        if (!input.IncludeArchived)
            query = query.Where(x => x.ArchivedAt == null);
        if (!string.IsNullOrWhiteSpace(input.Search))
        {
            var search = input.Search.Trim().ToLower();
            query = query.Where(x =>
                x.Name.ToLower().Contains(search) ||
                x.Description.ToLower().Contains(search));
        }
        var boards = await query.OrderByDescending(x => x.IsDefault)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);
        var result = boards.Select(board =>
        {
            var allowed = grants.Where(x =>
                    x.ScopeKind == GrantScopeKind.Organization ||
                    (x.ScopeKind == GrantScopeKind.Board && x.ScopeId == board.Id))
                .Select(x => x.Action)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
            return new Wire.WorkBoardSummary(
                board.Id, board.Name, board.Description, board.IsDefault,
                board.ArchivedAt.HasValue, board.Revision, allowed);
        }).ToList();
        await WriteAuditAsync(
            organizationId, installationId, null, WorkBoardActions.Read, null,
            new { count = result.Count, input.Search, input.IncludeArchived }, cancellationToken);
        return result;
    }

    private async Task<Wire.WorkBoardDetail> ReadBoardAsync(
        Guid organizationId,
        Guid installationId,
        Wire.WorkBoardReference input,
        CancellationToken cancellationToken)
    {
        var boardGrant = await RequireAsync(
            organizationId, installationId, WorkBoardActions.Read, input.BoardId, cancellationToken);
        var itemGrant = await RequireAsync(
            organizationId, installationId, WorkItemActions.Read, input.BoardId, cancellationToken);
        var board = await db.WorkBoards.AsNoTracking()
            .Include(x => x.Columns.OrderBy(column => column.Position))
            .SingleOrDefaultAsync(x =>
                x.Id == input.BoardId && x.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new KeyNotFoundException("Board was not found.");
        var itemRows = await db.CoreWorkTasks.AsNoTracking()
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
                x.DueDate
            })
            .ToListAsync(cancellationToken);
        var items = itemRows
            .Select(x => new Wire.WorkItem(
                x.Id, x.BoardColumnId!.Value, x.ParentWorkTaskId, x.SprintId,
                x.Kind.ToString(), x.Title, x.Description, x.Status.ToString(),
                x.Priority.ToString(), x.EstimatePoints, x.BoardRank, x.Revision,
                x.DueDate))
            .ToList();
        await WriteAuditAsync(
            organizationId, installationId, board.Id, WorkItemActions.Read, itemGrant,
            new { board.Id, itemCount = items.Count, boardGrantId = boardGrant.GrantId },
            cancellationToken);
        return new Wire.WorkBoardDetail(
            new Wire.WorkBoardSummary(
                board.Id, board.Name, board.Description, board.IsDefault,
                board.ArchivedAt.HasValue, board.Revision,
                [WorkBoardActions.Read, WorkItemActions.Read]),
            board.Columns.Select(x => new Wire.WorkBoardColumn(
                x.Id, x.Name, x.Category.ToString(), x.Position,
                x.WipPolicy.ToString(), x.WipLimit)).ToList(),
            items);
    }

    private async Task<IReadOnlyList<Wire.WorkSprint>> ListSprintsAsync(
        Guid organizationId,
        Guid installationId,
        Wire.WorkBoardReference input,
        CancellationToken cancellationToken)
    {
        await RequireAsync(
            organizationId, installationId, WorkBoardActions.Read,
            input.BoardId, cancellationToken);
        var grant = await RequireAsync(
            organizationId, installationId, WorkSprintActions.Read,
            input.BoardId, cancellationToken);
        if (!await db.WorkBoards.AsNoTracking().AnyAsync(x =>
                x.Id == input.BoardId && x.OrganizationId == organizationId,
                cancellationToken))
            throw new KeyNotFoundException("Board was not found.");
        var sprints = await db.WorkSprints.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.BoardId == input.BoardId)
            .OrderByDescending(x => x.Status == WorkSprintStatus.Active)
            .ThenByDescending(x => x.StartsAt)
            .ToListAsync(cancellationToken);
        var counts = await db.CoreWorkTasks.AsNoTracking()
            .Where(x => x.BoardId == input.BoardId && x.SprintId != null)
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
        var result = sprints.Select(x =>
        {
            counts.TryGetValue(x.Id, out var count);
            return ToAgentSprint(
                x, count?.Total ?? 0, count?.Completed ?? 0,
                count?.PlannedPoints ?? 0, count?.CompletedPoints ?? 0);
        }).ToList();
        await WriteAuditAsync(
            organizationId, installationId, input.BoardId, WorkSprintActions.Read,
            grant, new { count = result.Count }, cancellationToken);
        return result;
    }

    private async Task<Wire.WorkSprint> CreateSprintAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        Wire.CreateWorkSprintRequest input,
        CancellationToken cancellationToken)
    {
        var grant = await RequireAsync(
            organizationId, installation.Id, WorkSprintActions.Create,
            input.BoardId, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        var replay = await ReplaySprintAsync<Wire.WorkSprint>(
            installation.Id, WorkSprintActions.Create,
            input.IdempotencyKey, cancellationToken);
        if (replay is not null) return replay;
        if (string.IsNullOrWhiteSpace(input.Name))
            throw new ArgumentException("Sprint name is required.");
        if (input.Name.Trim().Length > 160)
            throw new ArgumentException("Sprint name cannot exceed 160 characters.");
        if ((input.Goal?.Trim().Length ?? 0) > 2048)
            throw new ArgumentException("Sprint goal cannot exceed 2048 characters.");
        if (input.StartsAt.HasValue && input.EndsAt.HasValue &&
            input.EndsAt <= input.StartsAt)
            throw new ArgumentException("Sprint end must be after its start.");
        if (!await db.WorkBoards.AnyAsync(x =>
                x.Id == input.BoardId &&
                x.OrganizationId == organizationId &&
                x.ArchivedAt == null, cancellationToken))
            throw new KeyNotFoundException("Board was not found.");

        var now = DateTimeOffset.UtcNow;
        var sprint = new WorkSprint
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            BoardId = input.BoardId,
            Name = input.Name.Trim(),
            Goal = input.Goal?.Trim() ?? string.Empty,
            StartsAt = input.StartsAt,
            EndsAt = input.EndsAt,
            CreatedAt = now,
            UpdatedAt = now
        };
        var result = ToAgentSprint(sprint, 0, 0);
        db.WorkSprints.Add(sprint);
        AddSprintReceipt(
            organizationId, installation.Id, WorkSprintActions.Create,
            input.IdempotencyKey, sprint.Id, result);
        await QueueRealtimeAsync(
            organizationId, input.BoardId, null, "sprint.created", sprint.Revision,
            cancellationToken, sprintId: sprint.Id);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, input.BoardId, WorkSprintActions.Create,
            grant, new { sprint.Id, sprint.Name, input.IdempotencyKey },
            cancellationToken, session);
        return result;
    }

    private async Task<Wire.WorkSprint> ChangeSprintStateAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        string action,
        Wire.ChangeWorkSprintStateRequest input,
        CancellationToken cancellationToken)
    {
        var grant = await RequireAsync(
            organizationId, installation.Id, action, input.BoardId, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        var replay = await ReplaySprintAsync<Wire.WorkSprint>(
            installation.Id, action, input.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (replay.Id != input.SprintId)
                throw new InvalidOperationException(
                    "The idempotency key was already used for a different sprint.");
            return replay;
        }
        var sprint = await db.WorkSprints.SingleOrDefaultAsync(x =>
            x.Id == input.SprintId &&
            x.OrganizationId == organizationId &&
            x.BoardId == input.BoardId, cancellationToken)
            ?? throw new KeyNotFoundException("Sprint was not found.");
        if (sprint.Revision != input.ExpectedRevision)
            throw new DbUpdateConcurrencyException(
                $"Expected sprint revision {input.ExpectedRevision}, current revision is {sprint.Revision}.");
        var now = DateTimeOffset.UtcNow;
        switch (action)
        {
            case WorkSprintActions.Start:
                if (sprint.Status != WorkSprintStatus.Planned)
                    throw new InvalidOperationException("Only a planned sprint can be started.");
                if (await db.WorkSprints.AnyAsync(x =>
                        x.BoardId == input.BoardId &&
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
        var total = await db.CoreWorkTasks.CountAsync(
            x => x.SprintId == sprint.Id, cancellationToken);
        var completed = await db.CoreWorkTasks.CountAsync(
            x => x.SprintId == sprint.Id &&
                 x.Status == WorkTaskStatus.Completed, cancellationToken);
        var plannedPoints = await db.CoreWorkTasks.Where(x => x.SprintId == sprint.Id)
            .SumAsync(x => x.EstimatePoints ?? 0, cancellationToken);
        var completedPoints = await db.CoreWorkTasks.Where(x =>
                x.SprintId == sprint.Id && x.Status == WorkTaskStatus.Completed)
            .SumAsync(x => x.EstimatePoints ?? 0, cancellationToken);
        if (action == WorkSprintActions.Complete)
            await WorkSprintSnapshotFactory.EnsureAsync(db, sprint, cancellationToken);
        await WorkSprintMetricsRecorder.RecordAsync(
            db, sprint.Id, SprintEventTypeFor(action), now, cancellationToken);
        var result = ToAgentSprint(
            sprint, total, completed, plannedPoints, completedPoints);
        AddSprintReceipt(
            organizationId, installation.Id, action,
            input.IdempotencyKey, sprint.Id, result);
        await QueueRealtimeAsync(
            organizationId, input.BoardId, null, SprintEventTypeFor(action),
            sprint.Revision, cancellationToken, sprintId: sprint.Id);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, input.BoardId, action, grant,
            new { sprint.Id, sprint.Status, sprint.Revision, input.IdempotencyKey },
            cancellationToken, session);
        return result;
    }

    private async Task<Wire.WorkItem> SetItemSprintAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        Wire.SetWorkItemSprintRequest input,
        CancellationToken cancellationToken)
    {
        var grant = await RequireAsync(
            organizationId, installation.Id, WorkSprintActions.ManageScope,
            input.BoardId, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        var replay = await ReplaySprintAsync<Wire.WorkItem>(
            installation.Id, WorkSprintActions.ManageScope,
            input.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (replay.Id != input.ItemId)
                throw new InvalidOperationException(
                    "The idempotency key was already used for a different work item.");
            return replay;
        }
        var item = await db.CoreWorkTasks.SingleOrDefaultAsync(x =>
            x.Id == input.ItemId &&
            x.OrganizationId == organizationId &&
            x.BoardId == input.BoardId, cancellationToken)
            ?? throw new KeyNotFoundException("Work item was not found.");
        if (item.Revision != input.ExpectedItemRevision)
            throw new DbUpdateConcurrencyException(
                $"Expected work item revision {input.ExpectedItemRevision}, current revision is {item.Revision}.");
        if (input.SprintId.HasValue && !await db.WorkSprints.AnyAsync(x =>
                x.Id == input.SprintId.Value &&
                x.OrganizationId == organizationId &&
                x.BoardId == input.BoardId &&
                (x.Status == WorkSprintStatus.Planned ||
                 x.Status == WorkSprintStatus.Active), cancellationToken))
            throw new ArgumentException(
                "The target sprint must be a planned or active sprint on this board.");

        var previousSprintId = item.SprintId;
        item.SprintId = input.SprintId;
        item.Revision++;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        var result = ToAgentItem(item);
        AddActivity(
            organizationId, input.BoardId, item.Id, installation.Id,
            string.IsNullOrWhiteSpace(session.AgentId) ? "Agent" : session.AgentId,
            WorkSprintActions.ManageScope,
            input.SprintId.HasValue ? "item.sprint.assigned" : "item.sprint.removed",
            grant, new { previousSprintId, sprintId = input.SprintId },
            item.UpdatedAt, input.IdempotencyKey);
        AddSprintReceipt(
            organizationId, installation.Id, WorkSprintActions.ManageScope,
            input.IdempotencyKey, item.Id, result);
        foreach (var sprintId in new[] { previousSprintId, input.SprintId }
                     .Where(x => x.HasValue).Distinct())
            await WorkSprintMetricsRecorder.RecordAsync(
                db, sprintId,
                input.SprintId.HasValue ? "item.sprint.assigned" : "item.sprint.removed",
                item.UpdatedAt, cancellationToken);
        await QueueRealtimeAsync(
            organizationId, input.BoardId, item.Id,
            input.SprintId.HasValue ? "item.sprint.assigned" : "item.sprint.removed",
            item.Revision, cancellationToken, sprintId: input.SprintId);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, input.BoardId,
            WorkSprintActions.ManageScope, grant,
            new
            {
                item.Id,
                previousSprintId,
                sprintId = input.SprintId,
                item.Revision,
                input.IdempotencyKey
            },
            cancellationToken, session);
        return result;
    }

    private async Task<Wire.WorkSprint> SetSprintCapacityAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        Wire.SetWorkSprintCapacityRequest input,
        CancellationToken cancellationToken)
    {
        ValidatePoints(input.CapacityPoints, "Capacity");
        var grant = await RequireAsync(
            organizationId, installation.Id, WorkSprintActions.ManageCapacity,
            input.BoardId, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        var replay = await ReplaySprintAsync<Wire.WorkSprint>(
            installation.Id, WorkSprintActions.ManageCapacity,
            input.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (replay.Id != input.SprintId)
                throw new InvalidOperationException(
                    "The idempotency key was already used for a different sprint.");
            return replay;
        }
        var sprint = await db.WorkSprints.SingleOrDefaultAsync(x =>
            x.Id == input.SprintId &&
            x.OrganizationId == organizationId &&
            x.BoardId == input.BoardId, cancellationToken)
            ?? throw new KeyNotFoundException("Sprint was not found.");
        if (sprint.Status is WorkSprintStatus.Completed or WorkSprintStatus.Cancelled)
            throw new InvalidOperationException(
                "Capacity cannot be changed after a sprint is closed.");
        if (sprint.Revision != input.ExpectedSprintRevision)
            throw new DbUpdateConcurrencyException(
                $"Expected sprint revision {input.ExpectedSprintRevision}, current revision is {sprint.Revision}.");
        var previousCapacity = sprint.CapacityPoints;
        sprint.CapacityPoints = input.CapacityPoints;
        sprint.Revision++;
        sprint.UpdatedAt = DateTimeOffset.UtcNow;
        var items = await db.CoreWorkTasks.Where(x => x.SprintId == sprint.Id)
            .ToListAsync(cancellationToken);
        var result = ToAgentSprint(
            sprint,
            items.Count,
            items.Count(x => x.Status == WorkTaskStatus.Completed),
            items.Sum(x => x.EstimatePoints ?? 0),
            items.Where(x => x.Status == WorkTaskStatus.Completed)
                .Sum(x => x.EstimatePoints ?? 0));
        AddSprintReceipt(
            organizationId, installation.Id, WorkSprintActions.ManageCapacity,
            input.IdempotencyKey, sprint.Id, result);
        await WorkSprintMetricsRecorder.RecordAsync(
            db, sprint.Id, "sprint.capacity.changed",
            sprint.UpdatedAt, cancellationToken);
        await QueueRealtimeAsync(
            organizationId, input.BoardId, null, "sprint.capacity.changed",
            sprint.Revision, cancellationToken, sprintId: sprint.Id);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, input.BoardId,
            WorkSprintActions.ManageCapacity, grant,
            new { sprint.Id, previousCapacity, capacityPoints = input.CapacityPoints },
            cancellationToken, session);
        return result;
    }

    private async Task<Wire.WorkSprintCarryOver> CarryOverSprintAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        Wire.CarryOverWorkSprintRequest input,
        CancellationToken cancellationToken)
    {
        if (input.SourceSprintId == input.TargetSprintId)
            throw new ArgumentException("Source and target sprint must be different.");
        var grant = await RequireAsync(
            organizationId, installation.Id, WorkSprintActions.CarryOver,
            input.BoardId, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        var replay = await ReplaySprintAsync<Wire.WorkSprintCarryOver>(
            installation.Id, WorkSprintActions.CarryOver,
            input.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (replay.SourceSprintId != input.SourceSprintId)
                throw new InvalidOperationException(
                    "The idempotency key was already used for a different carryover.");
            return replay;
        }
        var source = await db.WorkSprints.SingleOrDefaultAsync(x =>
            x.Id == input.SourceSprintId &&
            x.OrganizationId == organizationId &&
            x.BoardId == input.BoardId, cancellationToken)
            ?? throw new KeyNotFoundException("Source sprint was not found.");
        if (source.Status is not (WorkSprintStatus.Completed or WorkSprintStatus.Cancelled))
            throw new InvalidOperationException(
                "Only a completed or cancelled sprint can be carried over.");
        if (source.Revision != input.ExpectedSourceSprintRevision)
            throw new DbUpdateConcurrencyException(
                $"Expected sprint revision {input.ExpectedSourceSprintRevision}, current revision is {source.Revision}.");
        var target = await db.WorkSprints.SingleOrDefaultAsync(x =>
            x.Id == input.TargetSprintId &&
            x.OrganizationId == organizationId &&
            x.BoardId == input.BoardId &&
            (x.Status == WorkSprintStatus.Planned ||
             x.Status == WorkSprintStatus.Active), cancellationToken)
            ?? throw new ArgumentException(
                "The target must be a planned or active sprint on this board.");
        var requestedIds = input.ItemIds?.Distinct().ToHashSet();
        if (requestedIds?.Count > 500)
            throw new ArgumentException("At most 500 items can be carried over at once.");
        var candidates = await db.CoreWorkTasks.Where(x =>
                x.BoardId == input.BoardId &&
                x.SprintId == source.Id &&
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
            AddActivity(
                organizationId, input.BoardId, item.Id, installation.Id,
                string.IsNullOrWhiteSpace(session.AgentId) ? "Agent" : session.AgentId,
                WorkSprintActions.CarryOver, "item.sprint.carried-over", grant,
                new { sourceSprintId = source.Id, targetSprintId = target.Id }, now);
        }
        source.Revision++;
        source.UpdatedAt = now;
        target.Revision++;
        target.UpdatedAt = now;
        var result = new Wire.WorkSprintCarryOver(
            source.Id, target.Id, candidates.Select(x => x.Id).ToList(),
            candidates.Sum(x => x.EstimatePoints ?? 0));
        AddSprintReceipt(
            organizationId, installation.Id, WorkSprintActions.CarryOver,
            input.IdempotencyKey, source.Id, result);
        await WorkSprintMetricsRecorder.RecordAsync(
            db, source.Id, "sprint.items.carried-over",
            now, cancellationToken);
        await WorkSprintMetricsRecorder.RecordAsync(
            db, target.Id, "sprint.items.carried-over",
            now, cancellationToken);
        await QueueRealtimeAsync(
            organizationId, input.BoardId, null, "sprint.items.carried-over",
            target.Revision, cancellationToken, sprintId: target.Id);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, input.BoardId,
            WorkSprintActions.CarryOver, grant,
            new
            {
                sourceSprintId = source.Id,
                targetSprintId = target.Id,
                result.ItemIds,
                result.CarriedPoints
            },
            cancellationToken, session);
        return result;
    }

    private async Task<Wire.WorkSprintReport> ReadSprintReportAsync(
        Guid organizationId,
        Guid installationId,
        Wire.WorkBoardReference input,
        CancellationToken cancellationToken)
    {
        await RequireAsync(
            organizationId, installationId, WorkBoardActions.Read,
            input.BoardId, cancellationToken);
        var grant = await RequireAsync(
            organizationId, installationId, WorkSprintActions.ReadReports,
            input.BoardId, cancellationToken);
        if (!await db.WorkBoards.AsNoTracking().AnyAsync(x =>
                x.Id == input.BoardId && x.OrganizationId == organizationId,
                cancellationToken))
            throw new KeyNotFoundException("Board was not found.");
        var platformReport = await WorkSprintReportBuilder.BuildAsync(
            db, organizationId, input.BoardId, cancellationToken);
        var result = ToWireReport(platformReport);
        await WriteAuditAsync(
            organizationId, installationId, input.BoardId,
            WorkSprintActions.ReadReports, grant,
            new { result.CompletedSprintCount }, cancellationToken);
        return result;
    }

    private async Task<Wire.WorkAutomationDirectory> ReadAutomationsAsync(
        Guid organizationId,
        Guid installationId,
        Wire.WorkBoardReference input,
        CancellationToken cancellationToken)
    {
        await RequireAsync(
            organizationId, installationId, WorkBoardActions.Read,
            input.BoardId, cancellationToken);
        var grant = await RequireAsync(
            organizationId, installationId, WorkAutomationActions.Read,
            input.BoardId, cancellationToken);
        if (!await db.WorkBoards.AsNoTracking().AnyAsync(x =>
                x.Id == input.BoardId && x.OrganizationId == organizationId,
                cancellationToken))
            throw new KeyNotFoundException("Board was not found.");
        var rules = await db.WorkAutomationRules.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.BoardId == input.BoardId)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
        var ruleResponses = new List<Wire.WorkAutomationRule>(rules.Count);
        foreach (var rule in rules)
            ruleResponses.Add(await ToAutomationResponseAsync(rule, cancellationToken));
        var executionRows = await db.WorkAutomationExecutions.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.BoardId == input.BoardId)
            .OrderByDescending(x => x.CompletedAt)
            .Take(100)
            .ToListAsync(cancellationToken);
        var executions = executionRows
            .Select(x => new Wire.WorkAutomationExecution(
                x.Id, x.RuleId, x.SourceActivityId, x.WorkItemId,
                x.Status.ToString(), x.RequiredAction,
                x.AuthorizingGrantId, x.AuthorizingGrantRevision,
                x.ErrorCode, x.ErrorMessage, x.CompletedAt))
            .ToList();
        await WriteAuditAsync(
            organizationId, installationId, input.BoardId,
            WorkAutomationActions.Read, grant,
            new { ruleCount = ruleResponses.Count, executionCount = executions.Count },
            cancellationToken);
        return new Wire.WorkAutomationDirectory(ruleResponses, executions);
    }

    private async Task<Wire.WorkAutomationRule> ManageAutomationAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        Wire.ManageWorkAutomationRequest input,
        CancellationToken cancellationToken)
    {
        var grant = await RequireAsync(
            organizationId, installation.Id, WorkAutomationActions.Manage,
            input.BoardId, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        var replay = await ReplaySprintAsync<Wire.WorkAutomationRule>(
            installation.Id, WorkAutomationActions.Manage,
            input.IdempotencyKey, cancellationToken);
        if (replay is not null) return replay;

        WorkAutomationRule rule;
        var operation = input.Operation?.Trim().ToLowerInvariant();
        if (operation == "create")
        {
            await ValidateAutomationAsync(
                organizationId, input.BoardId, input.Name, input.TriggerEventType,
                input.ConditionColumnId, input.Action, input.TargetColumnId,
                cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid();
            rule = new WorkAutomationRule
            {
                Id = id,
                OrganizationId = organizationId,
                BoardId = input.BoardId,
                AutomationIdentityId = id,
                Name = input.Name!.Trim(),
                TriggerEventType = input.TriggerEventType!.Trim(),
                ConditionColumnId = input.ConditionColumnId,
                Action = input.Action!.Trim(),
                TargetColumnId = input.TargetColumnId!.Value,
                IsEnabled = input.IsEnabled ?? false,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.WorkAutomationRules.Add(rule);
        }
        else
        {
            if (!input.RuleId.HasValue)
                throw new ArgumentException("ruleId is required for update or delete.");
            rule = await db.WorkAutomationRules.SingleOrDefaultAsync(x =>
                x.Id == input.RuleId.Value &&
                x.OrganizationId == organizationId &&
                x.BoardId == input.BoardId, cancellationToken)
                ?? throw new KeyNotFoundException("Automation rule was not found.");
            if (!input.ExpectedRevision.HasValue ||
                rule.Revision != input.ExpectedRevision.Value)
                throw new DbUpdateConcurrencyException(
                    "The automation rule changed since it was loaded.");
            if (operation == "update")
            {
                await ValidateAutomationAsync(
                    organizationId, input.BoardId, input.Name, input.TriggerEventType,
                    input.ConditionColumnId, input.Action, input.TargetColumnId,
                    cancellationToken);
                rule.Name = input.Name!.Trim();
                rule.TriggerEventType = input.TriggerEventType!.Trim();
                rule.ConditionColumnId = input.ConditionColumnId;
                rule.Action = input.Action!.Trim();
                rule.TargetColumnId = input.TargetColumnId!.Value;
                rule.IsEnabled = input.IsEnabled ?? rule.IsEnabled;
                rule.Revision++;
                rule.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else if (operation == "delete")
            {
                if (await db.WorkAutomationExecutions.AnyAsync(
                        x => x.RuleId == rule.Id, cancellationToken))
                    throw new InvalidOperationException(
                        "Rules with execution history cannot be deleted; disable the rule instead.");
            }
            else
            {
                throw new ArgumentException("operation must be Create, Update, or Delete.");
            }
        }

        var result = await ToAutomationResponseAsync(rule, cancellationToken);
        AddSprintReceipt(
            organizationId, installation.Id, WorkAutomationActions.Manage,
            input.IdempotencyKey, rule.Id, result);
        if (operation == "delete")
            db.WorkAutomationRules.Remove(rule);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, input.BoardId,
            WorkAutomationActions.Manage, grant,
            new
            {
                operation,
                rule.Id,
                rule.AutomationIdentityId,
                rule.Action,
                input.IdempotencyKey
            },
            cancellationToken, session);
        return result;
    }

    private async Task ValidateAutomationAsync(
        Guid organizationId,
        Guid boardId,
        string? name,
        string? trigger,
        Guid? conditionColumnId,
        string? action,
        Guid? targetColumnId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 160)
            throw new ArgumentException("A rule name of at most 160 characters is required.");
        var supportedTriggers = new HashSet<string>(StringComparer.Ordinal)
        {
            "item.created", "item.moved", "item.completed", "item.cancelled",
            "item.reopened", "item.sprint.assigned", "item.sprint.removed",
            "item.estimate.changed", "comment.created"
        };
        if (!supportedTriggers.Contains(trigger?.Trim() ?? string.Empty))
            throw new ArgumentException("The automation trigger is not supported.");
        var supportedActions = new[]
        {
            WorkItemActions.Move, WorkItemActions.Complete,
            WorkItemActions.Cancel, WorkItemActions.Reopen
        };
        if (!supportedActions.Contains(action?.Trim(), StringComparer.Ordinal))
            throw new ArgumentException("The automation action is not supported.");
        if (!targetColumnId.HasValue)
            throw new ArgumentException("targetColumnId is required.");
        var columns = await db.WorkBoardColumns.AsNoTracking()
            .Where(x => x.BoardId == boardId &&
                        x.Board!.OrganizationId == organizationId)
            .ToListAsync(cancellationToken);
        if (conditionColumnId.HasValue &&
            columns.All(x => x.Id != conditionColumnId.Value))
            throw new ArgumentException("The condition column does not belong to this board.");
        var target = columns.SingleOrDefault(x => x.Id == targetColumnId.Value)
            ?? throw new ArgumentException("The target column does not belong to this board.");
        var valid = action!.Trim() switch
        {
            WorkItemActions.Complete => target.Category == WorkBoardColumnCategory.Done,
            WorkItemActions.Cancel => target.Category == WorkBoardColumnCategory.Cancelled,
            WorkItemActions.Move or WorkItemActions.Reopen =>
                target.Category is WorkBoardColumnCategory.ToDo or WorkBoardColumnCategory.InProgress,
            _ => false
        };
        if (!valid)
            throw new ArgumentException("The target column is incompatible with the action.");
    }

    private async Task<Wire.WorkAutomationRule> ToAutomationResponseAsync(
        WorkAutomationRule rule,
        CancellationToken cancellationToken)
    {
        var executionGrant = await authorization.AuthorizeAsync(
            rule.OrganizationId, GrantSubjectKind.AutomationIdentity,
            rule.AutomationIdentityId, rule.Action,
            GrantScopeKind.Board, rule.BoardId, cancellationToken);
        return new Wire.WorkAutomationRule(
            rule.Id, rule.BoardId, rule.AutomationIdentityId, rule.Name,
            rule.TriggerEventType, rule.ConditionColumnId, rule.Action,
            rule.TargetColumnId, rule.IsEnabled, executionGrant.Allowed,
            rule.Revision, rule.CreatedAt, rule.UpdatedAt);
    }

    private async Task<Wire.WorkBoardSummary> CreateBoardAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        Wire.CreateWorkBoardRequest input,
        CancellationToken cancellationToken)
    {
        var grant = await RequireAsync(
            organizationId, installation.Id, WorkBoardActions.Create, null, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        if (string.IsNullOrWhiteSpace(input.Name))
            throw new ArgumentException("Board name is required.");
        var replay = await ReplayAsync<Wire.WorkBoardSummary>(
            installation.Id, WorkBoardActions.Create, input.IdempotencyKey, cancellationToken);
        if (replay is not null) return replay;

        var now = DateTimeOffset.UtcNow;
        var board = new WorkBoard
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Name = input.Name.Trim(),
            Description = input.Description?.Trim() ?? string.Empty,
            CreatedAt = now,
            UpdatedAt = now,
            Columns =
            [
                NewColumn("To Do", WorkBoardColumnCategory.ToDo, 0),
                NewColumn("Done", WorkBoardColumnCategory.Done, 1)
            ]
        };
        var result = new Wire.WorkBoardSummary(
            board.Id, board.Name, board.Description, false, false, board.Revision,
            [WorkBoardActions.Create]);
        db.WorkBoards.Add(board);
        AddReceipt(
            organizationId, installation.Id, WorkBoardActions.Create,
            input.IdempotencyKey, board.Id, result);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, board.Id, WorkBoardActions.Create, grant,
            new { board.Id, board.Name, input.IdempotencyKey }, cancellationToken, session);
        return result;
    }

    private async Task<Wire.WorkItem> CreateItemAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        Wire.CreateWorkItemRequest input,
        CancellationToken cancellationToken)
    {
        var grant = await RequireAsync(
            organizationId, installation.Id, WorkItemActions.Create, input.BoardId, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        if (string.IsNullOrWhiteSpace(input.Title))
            throw new ArgumentException("Work item title is required.");
        if (!Enum.TryParse<WorkItemKind>(input.Kind, true, out var kind) || !Enum.IsDefined(kind))
            throw new ArgumentException("Work item kind is invalid.");
        if (!Enum.TryParse<WorkTaskPriority>(input.Priority, true, out var priority) ||
            !Enum.IsDefined(priority))
            throw new ArgumentException("Work item priority is invalid.");
        var replay = await ReplayAsync<Wire.WorkItem>(
            installation.Id, WorkItemActions.Create, input.IdempotencyKey, cancellationToken);
        if (replay is not null) return replay;

        var board = await db.WorkBoards
            .Include(x => x.Columns)
            .SingleOrDefaultAsync(x =>
                x.Id == input.BoardId &&
                x.OrganizationId == organizationId &&
                x.ArchivedAt == null, cancellationToken)
            ?? throw new KeyNotFoundException("Board was not found.");
        var column = input.ColumnId.HasValue
            ? board.Columns.SingleOrDefault(x => x.Id == input.ColumnId.Value)
            : board.Columns.OrderBy(x => x.Position)
                .FirstOrDefault(x => x.Category == WorkBoardColumnCategory.ToDo);
        if (column is null)
            throw new ArgumentException("The requested board column was not found.");
        await EnforceWipAsync(column, null, cancellationToken);
        if (input.ParentItemId.HasValue && !await db.CoreWorkTasks.AnyAsync(x =>
                x.Id == input.ParentItemId &&
                x.OrganizationId == organizationId &&
                x.BoardId == board.Id, cancellationToken))
            throw new ArgumentException("The parent work item must belong to the same board.");

        var now = DateTimeOffset.UtcNow;
        var item = new WorkTask
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            BoardId = board.Id,
            BoardColumnId = column.Id,
            ParentWorkTaskId = input.ParentItemId,
            Kind = kind,
            Title = input.Title.Trim(),
            Description = input.Description?.Trim() ?? string.Empty,
            Status = StatusFor(column.Category),
            Priority = priority,
            BoardRank = (await db.CoreWorkTasks
                .Where(x => x.BoardColumnId == column.Id)
                .MaxAsync(x => (long?)x.BoardRank, cancellationToken) ?? 0) + 1024,
            DueDate = input.DueDate,
            CreatedAt = now,
            UpdatedAt = now
        };
        var result = ToAgentItem(item);
        db.CoreWorkTasks.Add(item);
        AddActivity(
            organizationId, board.Id, item.Id, installation.Id,
            string.IsNullOrWhiteSpace(session.AgentId) ? "Agent" : session.AgentId,
            WorkItemActions.Create, "item.created", grant,
            new { columnId = column.Id }, now);
        AddReceipt(
            organizationId, installation.Id, WorkItemActions.Create,
            input.IdempotencyKey, item.Id, result);
        await QueueRealtimeAsync(
            organizationId, board.Id, item.Id, "item.created",
            item.Revision, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, board.Id, WorkItemActions.Create, grant,
            new { boardId = board.Id, itemId = item.Id, item.Kind, input.IdempotencyKey },
            cancellationToken, session);
        return result;
    }

    private async Task<Wire.WorkItemComment> CommentItemAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        Wire.CommentOnWorkItemRequest input,
        CancellationToken cancellationToken)
    {
        var grant = await RequireAsync(
            organizationId, installation.Id, WorkItemActions.Comment,
            input.BoardId, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        if (string.IsNullOrWhiteSpace(input.Body))
            throw new ArgumentException("Comment body is required.");
        if (input.Body.Trim().Length > 8192)
            throw new ArgumentException("Comment body cannot exceed 8192 characters.");
        var replay = await ReplayAsync<Wire.WorkItemComment>(
            installation.Id, WorkItemActions.Comment, input.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (replay.WorkItemId != input.ItemId)
                throw new InvalidOperationException(
                    "The idempotency key was already used for a different work item comment.");
            return replay;
        }

        var item = await db.CoreWorkTasks.SingleOrDefaultAsync(x =>
            x.Id == input.ItemId &&
            x.OrganizationId == organizationId &&
            x.BoardId == input.BoardId, cancellationToken)
            ?? throw new KeyNotFoundException("Work item was not found.");
        var now = DateTimeOffset.UtcNow;
        var comment = new WorkItemComment
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            WorkItemId = item.Id,
            AuthorKind = GrantSubjectKind.AgentInstallation,
            AuthorSubjectId = installation.Id,
            AuthorDisplayName = string.IsNullOrWhiteSpace(session.AgentId)
                ? "Agent"
                : session.AgentId,
            Body = input.Body.Trim(),
            IdempotencyKey = input.IdempotencyKey,
            CreatedAt = now
        };
        var result = new Wire.WorkItemComment(
            comment.Id, comment.WorkItemId, comment.AuthorKind.ToString(),
            comment.AuthorSubjectId, comment.AuthorDisplayName, comment.Body,
            comment.Revision, comment.CreatedAt, comment.EditedAt);
        db.WorkItemComments.Add(comment);
        AddActivity(
            organizationId, input.BoardId, item.Id, installation.Id,
            comment.AuthorDisplayName, WorkItemActions.Comment, "comment.created",
            grant, new { commentId = comment.Id }, now);
        AddReceipt(
            organizationId, installation.Id, WorkItemActions.Comment,
            input.IdempotencyKey, comment.Id, result);
        await QueueRealtimeAsync(
            organizationId, input.BoardId, item.Id, "comment.created",
            item.Revision, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, input.BoardId, WorkItemActions.Comment,
            grant, new { itemId = item.Id, commentId = comment.Id, input.IdempotencyKey },
            cancellationToken, session);
        return result;
    }

    private async Task<Wire.WorkItem> EstimateItemAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        Wire.EstimateWorkItemRequest input,
        CancellationToken cancellationToken)
    {
        ValidatePoints(input.EstimatePoints, "Estimate");
        var grant = await RequireAsync(
            organizationId, installation.Id, WorkItemActions.Estimate,
            input.BoardId, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        var replay = await ReplaySprintAsync<Wire.WorkItem>(
            installation.Id, WorkItemActions.Estimate,
            input.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (replay.Id != input.ItemId)
                throw new InvalidOperationException(
                    "The idempotency key was already used for a different work item.");
            return replay;
        }
        var item = await db.CoreWorkTasks.SingleOrDefaultAsync(x =>
            x.Id == input.ItemId &&
            x.OrganizationId == organizationId &&
            x.BoardId == input.BoardId, cancellationToken)
            ?? throw new KeyNotFoundException("Work item was not found.");
        if (item.Revision != input.ExpectedItemRevision)
            throw new DbUpdateConcurrencyException(
                $"Expected work item revision {input.ExpectedItemRevision}, current revision is {item.Revision}.");
        var previousEstimate = item.EstimatePoints;
        item.EstimatePoints = input.EstimatePoints;
        item.Revision++;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        var result = ToAgentItem(item);
        AddActivity(
            organizationId, input.BoardId, item.Id, installation.Id,
            string.IsNullOrWhiteSpace(session.AgentId) ? "Agent" : session.AgentId,
            WorkItemActions.Estimate, "item.estimate.changed", grant,
            new { previousEstimate, estimatePoints = input.EstimatePoints },
            item.UpdatedAt, input.IdempotencyKey);
        AddSprintReceipt(
            organizationId, installation.Id, WorkItemActions.Estimate,
            input.IdempotencyKey, item.Id, result);
        await WorkSprintMetricsRecorder.RecordAsync(
            db, item.SprintId, "item.estimate.changed",
            item.UpdatedAt, cancellationToken);
        await QueueRealtimeAsync(
            organizationId, input.BoardId, item.Id, "item.estimate.changed",
            item.Revision, cancellationToken, sprintId: item.SprintId);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, input.BoardId,
            WorkItemActions.Estimate, grant,
            new { item.Id, previousEstimate, estimatePoints = input.EstimatePoints },
            cancellationToken, session);
        return result;
    }

    private async Task<Wire.WorkItemTransfer> TransferItemAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        Wire.TransferWorkItemRequest input,
        CancellationToken cancellationToken)
    {
        if (input.BoardId == input.TargetBoardId)
            throw new ArgumentException(
                "Use move_work_item when the source and target board are the same.");
        var sourceGrant = await RequireAsync(
            organizationId, installation.Id, WorkItemActions.Transfer,
            input.BoardId, cancellationToken);
        var targetGrant = await RequireAsync(
            organizationId, installation.Id, WorkItemActions.Transfer,
            input.TargetBoardId, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        var replay = await ReplayAsync<Wire.WorkItemTransfer>(
            installation.Id, WorkItemActions.Transfer,
            input.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (replay.Item.Id != input.ItemId)
                throw new InvalidOperationException(
                    "The idempotency key was already used for a different work item transfer.");
            return replay;
        }

        var item = await db.CoreWorkTasks.SingleOrDefaultAsync(x =>
            x.Id == input.ItemId &&
            x.OrganizationId == organizationId &&
            x.BoardId == input.BoardId, cancellationToken)
            ?? throw new KeyNotFoundException("Work item was not found.");
        if (item.Revision != input.ExpectedRevision)
            throw new DbUpdateConcurrencyException(
                $"Expected work item revision {input.ExpectedRevision}, current revision is {item.Revision}.");
        if (item.ParentWorkTaskId.HasValue ||
            await db.CoreWorkTasks.AnyAsync(
                x => x.ParentWorkTaskId == item.Id, cancellationToken))
            throw new InvalidOperationException(
                "A hierarchical work item must be detached or transferred with its hierarchy.");

        var targetBoard = await db.WorkBoards
            .Include(x => x.Columns)
            .SingleOrDefaultAsync(x =>
                x.Id == input.TargetBoardId &&
                x.OrganizationId == organizationId &&
                x.ArchivedAt == null, cancellationToken)
            ?? throw new KeyNotFoundException("Target board was not found.");
        var targetColumn = input.TargetColumnId.HasValue
            ? targetBoard.Columns.SingleOrDefault(x => x.Id == input.TargetColumnId.Value)
            : targetBoard.Columns.OrderBy(x => x.Position)
                .FirstOrDefault(x => x.Category == WorkBoardColumnCategory.ToDo);
        if (targetColumn is null)
            throw new ArgumentException(
                "The target column does not belong to the target board.");
        await EnforceWipAsync(targetColumn, null, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var sourceColumnId = item.BoardColumnId;
        var sourceSprintId = item.SprintId;
        item.BoardId = targetBoard.Id;
        item.BoardColumnId = targetColumn.Id;
        item.SprintId = null;
        item.BoardRank = (await db.CoreWorkTasks
            .Where(x => x.BoardColumnId == targetColumn.Id)
            .MaxAsync(x => (long?)x.BoardRank, cancellationToken) ?? 0) + 1024;
        item.Status = StatusFor(targetColumn.Category);
        item.Revision++;
        item.UpdatedAt = now;
        var result = new Wire.WorkItemTransfer(
            input.BoardId, targetBoard.Id, ToAgentItem(item));
        AddActivity(
            organizationId, targetBoard.Id, item.Id, installation.Id,
            string.IsNullOrWhiteSpace(session.AgentId) ? "Agent" : session.AgentId,
            WorkItemActions.Transfer, "item.transferred", targetGrant,
            new
            {
                sourceBoardId = input.BoardId,
                sourceColumnId,
                sourceSprintId,
                targetBoardId = targetBoard.Id,
                targetColumnId = targetColumn.Id,
                sourceGrantId = sourceGrant.GrantId
            },
            now,
            input.IdempotencyKey);
        AddReceipt(
            organizationId, installation.Id, WorkItemActions.Transfer,
            input.IdempotencyKey, item.Id, result);
        await QueueRealtimeAsync(
            organizationId, input.BoardId, item.Id, "item.transferred.out",
            item.Revision, cancellationToken, targetBoard.Id);
        await QueueRealtimeAsync(
            organizationId, targetBoard.Id, item.Id, "item.transferred.in",
            item.Revision, cancellationToken, input.BoardId);
        await WorkSprintMetricsRecorder.RecordAsync(
            db, sourceSprintId, "item.transferred.out",
            now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, targetBoard.Id, WorkItemActions.Transfer,
            targetGrant,
            new
            {
                sourceBoardId = input.BoardId,
                targetBoardId = targetBoard.Id,
                targetColumnId = targetColumn.Id,
                itemId = item.Id,
                input.IdempotencyKey
            },
            cancellationToken, session);
        return result;
    }

    private async Task<Wire.WorkItem> TransitionItemAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        string action,
        Wire.TransitionWorkItemRequest input,
        CancellationToken cancellationToken)
    {
        var grant = await RequireAsync(
            organizationId, installation.Id, action, input.BoardId, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        var replay = await ReplayAsync<Wire.WorkItem>(
            installation.Id, action, input.IdempotencyKey, cancellationToken);
        if (replay is not null) return replay;

        var item = await db.CoreWorkTasks.SingleOrDefaultAsync(x =>
            x.Id == input.ItemId &&
            x.OrganizationId == organizationId &&
            x.BoardId == input.BoardId, cancellationToken)
            ?? throw new KeyNotFoundException("Work item was not found.");
        if (item.Revision != input.ExpectedRevision)
            throw new DbUpdateConcurrencyException(
                $"Expected work item revision {input.ExpectedRevision}, current revision is {item.Revision}.");
        var columns = await db.WorkBoardColumns
            .Where(x => x.BoardId == input.BoardId)
            .OrderBy(x => x.Position)
            .ToListAsync(cancellationToken);
        var target = ResolveTransitionColumn(action, input.TargetColumnId, item, columns);
        await EnforceWipAsync(target, item.Id, cancellationToken);

        var sourceColumnId = item.BoardColumnId;
        item.BoardColumnId = target.Id;
        item.BoardRank = (await db.CoreWorkTasks
            .Where(x => x.BoardColumnId == target.Id && x.Id != item.Id)
            .MaxAsync(x => (long?)x.BoardRank, cancellationToken) ?? 0) + 1024;
        item.Status = StatusFor(target.Category);
        item.Revision++;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        var result = ToAgentItem(item);
        AddActivity(
            organizationId, input.BoardId, item.Id, installation.Id,
            string.IsNullOrWhiteSpace(session.AgentId) ? "Agent" : session.AgentId,
            action, EventTypeFor(action), grant,
            new { sourceColumnId, targetColumnId = target.Id, item.BoardRank },
            item.UpdatedAt);
        AddReceipt(
            organizationId, installation.Id, action,
            input.IdempotencyKey, item.Id, result);
        await QueueRealtimeAsync(
            organizationId, input.BoardId, item.Id, EventTypeFor(action),
            item.Revision, cancellationToken);
        await WorkSprintMetricsRecorder.RecordAsync(
            db, item.SprintId, EventTypeFor(action),
            item.UpdatedAt, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, input.BoardId, action, grant,
            new
            {
                input.BoardId,
                item.Id,
                targetColumnId = target.Id,
                item.Revision,
                input.IdempotencyKey
            }, cancellationToken, session);
        return result;
    }

    private static WorkBoardColumn ResolveTransitionColumn(
        string action,
        Guid? targetColumnId,
        WorkTask item,
        IReadOnlyList<WorkBoardColumn> columns)
    {
        WorkBoardColumn? target = targetColumnId.HasValue
            ? columns.SingleOrDefault(x => x.Id == targetColumnId.Value)
            : action switch
            {
                WorkItemActions.Complete => columns.FirstOrDefault(x =>
                    x.Category == WorkBoardColumnCategory.Done),
                WorkItemActions.Cancel => columns.FirstOrDefault(x =>
                    x.Category == WorkBoardColumnCategory.Cancelled),
                WorkItemActions.Reopen => columns.FirstOrDefault(x =>
                    x.Category == WorkBoardColumnCategory.ToDo),
                _ => null
            };
        if (target is null)
            throw new ArgumentException("A valid target column is required for this transition.");
        var valid = action switch
        {
            WorkItemActions.Complete => target.Category == WorkBoardColumnCategory.Done,
            WorkItemActions.Cancel => target.Category == WorkBoardColumnCategory.Cancelled,
            WorkItemActions.Reopen =>
                item.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled &&
                target.Category is WorkBoardColumnCategory.ToDo or WorkBoardColumnCategory.InProgress,
            WorkItemActions.Move =>
                item.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled) &&
                target.Category is WorkBoardColumnCategory.ToDo or WorkBoardColumnCategory.InProgress,
            _ => false
        };
        if (!valid)
            throw new UnauthorizedAccessException(
                $"The '{action}' grant cannot perform the requested state transition.");
        return target;
    }

    private void AddActivity(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        Guid installationId,
        string actorDisplayName,
        string action,
        string eventType,
        ScopedAuthorizationDecision grant,
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
            ActorKind = GrantSubjectKind.AgentInstallation,
            ActorSubjectId = installationId,
            ActorDisplayName = actorDisplayName,
            AuthorizingGrantId = grant.GrantId,
            AuthorizingGrantRevision = grant.GrantRevision,
            IdempotencyKey = idempotencyKey,
            DataJson = JsonSerializer.Serialize(data, JsonOptions),
            OccurredAt = occurredAt
        });

    private async Task QueueRealtimeAsync(
        Guid organizationId,
        Guid boardId,
        Guid? itemId,
        string changeType,
        long revision,
        CancellationToken cancellationToken,
        Guid? relatedBoardId = null,
        Guid? sprintId = null)
    {
        var recipients = await ResolveRealtimeRecipientsAsync(
            organizationId, boardId, cancellationToken,
            requireSprintRead: sprintId.HasValue && !itemId.HasValue);
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
                revision,
                relatedBoardId,
                sprintId
            }, JsonOptions),
            Status = ApplicationRealtimeOutboxStatus.Pending,
            NextAttemptAt = now,
            OccurredAt = now
        });
    }

    private async Task<IReadOnlyList<Guid>> ResolveRealtimeRecipientsAsync(
        Guid organizationId,
        Guid boardId,
        CancellationToken cancellationToken,
        bool requireSprintRead = false)
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
        var detailReaders = grants.Where(x => x.Action ==
                (requireSprintRead ? WorkSprintActions.Read : WorkItemActions.Read))
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

    private async Task<ScopedAuthorizationDecision> RequireAsync(
        Guid organizationId,
        Guid installationId,
        string action,
        Guid? boardId,
        CancellationToken cancellationToken)
    {
        var decision = await authorization.AuthorizeAsync(
            organizationId,
            GrantSubjectKind.AgentInstallation,
            installationId,
            action,
            boardId.HasValue ? GrantScopeKind.Board : GrantScopeKind.Organization,
            boardId,
            cancellationToken);
        if (decision.Allowed) return decision;
        await WriteAuditAsync(
            organizationId, installationId, boardId, action, null,
            new { action, boardId }, cancellationToken, outcome: "Denied");
        throw new UnauthorizedAccessException(
            $"The installation does not have '{action}' on the requested scope.");
    }

    private async Task<List<ScopedActionGrant>> ActiveGrantsAsync(
        Guid organizationId,
        Guid installationId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.ScopedActionGrants.AsNoTracking().Where(x =>
            x.OrganizationId == organizationId &&
            x.SubjectKind == GrantSubjectKind.AgentInstallation &&
            x.SubjectId == installationId &&
            x.RevokedAt == null &&
            (!x.ExpiresAt.HasValue || x.ExpiresAt > now))
            .ToListAsync(cancellationToken);
    }

    private async Task EnforceWipAsync(
        WorkBoardColumn column,
        Guid? excludedItemId,
        CancellationToken cancellationToken)
    {
        if (column.WipPolicy != WorkBoardWipPolicy.HardLimit || !column.WipLimit.HasValue)
            return;
        var count = await db.CoreWorkTasks.CountAsync(x =>
            x.BoardColumnId == column.Id &&
            (!excludedItemId.HasValue || x.Id != excludedItemId.Value), cancellationToken);
        if (count >= column.WipLimit.Value)
            throw new InvalidOperationException(
                $"Column '{column.Name}' has reached its WIP limit of {column.WipLimit.Value}.");
    }

    private async Task<T?> ReplayAsync<T>(
        Guid installationId,
        string action,
        string idempotencyKey,
        CancellationToken cancellationToken) where T : class
    {
        var json = await db.WorkItemMutationReceipts.AsNoTracking()
            .Where(x =>
                x.AgentInstallationId == installationId &&
                x.Action == action &&
                x.IdempotencyKey == idempotencyKey)
            .Select(x => x.ResultJson)
            .SingleOrDefaultAsync(cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private void AddReceipt<T>(
        Guid organizationId,
        Guid installationId,
        string action,
        string idempotencyKey,
        Guid resourceId,
        T result) =>
        db.WorkItemMutationReceipts.Add(new WorkItemMutationReceipt
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            AgentInstallationId = installationId,
            Action = action,
            IdempotencyKey = idempotencyKey,
            ResourceId = resourceId,
            ResultJson = JsonSerializer.Serialize(result, JsonOptions),
            CreatedAt = DateTimeOffset.UtcNow
        });

    private async Task<T?> ReplaySprintAsync<T>(
        Guid installationId,
        string action,
        string idempotencyKey,
        CancellationToken cancellationToken) where T : class
    {
        var json = await db.WorkSprintMutationReceipts.AsNoTracking()
            .Where(x =>
                x.ActorKind == GrantSubjectKind.AgentInstallation &&
                x.ActorSubjectId == installationId &&
                x.Action == action &&
                x.IdempotencyKey == idempotencyKey)
            .Select(x => x.ResultJson)
            .SingleOrDefaultAsync(cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private void AddSprintReceipt<T>(
        Guid organizationId,
        Guid installationId,
        string action,
        string idempotencyKey,
        Guid resourceId,
        T result) =>
        db.WorkSprintMutationReceipts.Add(new WorkSprintMutationReceipt
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ActorKind = GrantSubjectKind.AgentInstallation,
            ActorSubjectId = installationId,
            Action = action,
            IdempotencyKey = idempotencyKey,
            ResourceId = resourceId,
            ResultJson = JsonSerializer.Serialize(result, JsonOptions),
            CreatedAt = DateTimeOffset.UtcNow
        });

    private Task WriteAuditAsync(
        Guid organizationId,
        Guid installationId,
        Guid? boardId,
        string action,
        ScopedAuthorizationDecision? grant,
        object metadata,
        CancellationToken cancellationToken,
        AgentSession? session = null,
        string outcome = "Completed") =>
        audit.AppendAsync(new AuditEventWriteRequest(
            action,
            "WorkManagement",
            "Inbound",
            outcome,
            organizationId,
            boardId.HasValue ? "WorkBoard" : "Organization",
            boardId ?? organizationId,
            $"{outcome} {action}.",
            JsonSerializer.Serialize(new
            {
                action,
                grantId = grant?.GrantId,
                grantRevision = grant?.GrantRevision,
                data = metadata
            }, JsonOptions),
            Actor: new AuditActor(
                "Agent",
                true,
                AgentId: session?.AgentId,
                InstallationId: installationId,
                RuntimeInstanceId: ParseGuid(session?.RuntimeInstanceId),
                TickId: ParseGuid(session?.TickId),
                SessionId: session?.SessionId)),
            cancellationToken);

    private static T Read<T>(RequestCapability request) =>
        JsonSerializer.Deserialize<T>(request.Payload.Span, JsonOptions)
        ?? throw new ArgumentException("The capability payload is required.");

    private static void ValidateIdempotencyKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160)
            throw new ArgumentException("A non-empty idempotency key of at most 160 characters is required.");
    }

    private static void ValidatePoints(decimal? points, string label)
    {
        if (points is < 0 or > 999999.99m)
            throw new ArgumentException(
                $"{label} points must be between 0 and 999999.99.");
    }

    private static WorkTaskStatus StatusFor(WorkBoardColumnCategory category) => category switch
    {
        WorkBoardColumnCategory.ToDo => WorkTaskStatus.Ready,
        WorkBoardColumnCategory.InProgress => WorkTaskStatus.Running,
        WorkBoardColumnCategory.Done => WorkTaskStatus.Completed,
        WorkBoardColumnCategory.Cancelled => WorkTaskStatus.Cancelled,
        _ => WorkTaskStatus.Ready
    };

    private static string EventTypeFor(string action) => action switch
    {
        WorkItemActions.Complete => "item.completed",
        WorkItemActions.Cancel => "item.cancelled",
        WorkItemActions.Reopen => "item.reopened",
        _ => "item.moved"
    };

    private static string SprintEventTypeFor(string action) => action switch
    {
        WorkSprintActions.Start => "sprint.started",
        WorkSprintActions.Complete => "sprint.completed",
        WorkSprintActions.Cancel => "sprint.cancelled",
        _ => "sprint.changed"
    };

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

    private static Wire.WorkItem ToAgentItem(WorkTask item) => new(
        item.Id,
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
        item.DueDate);

    private static Wire.WorkSprint ToAgentSprint(
        WorkSprint sprint,
        int itemCount,
        int completedItemCount,
        decimal plannedPoints = 0,
        decimal completedPoints = 0) => new(
        sprint.Id, sprint.BoardId, sprint.Name, sprint.Goal,
        sprint.Status.ToString(), sprint.StartsAt, sprint.EndsAt,
        sprint.StartedAt, sprint.CompletedAt, sprint.CapacityPoints,
        itemCount, completedItemCount, plannedPoints, completedPoints, sprint.Revision);

    private static Wire.WorkSprintReport ToWireReport(
        WorkSprintReportResponse report) => new(
        report.BoardId,
        report.CompletedSprintCount,
        report.AverageVelocity,
        report.TotalCompletedPoints,
        report.AverageCapacityUtilizationPercent,
        report.Sprints.Select(snapshot => new Wire.WorkSprintSnapshot(
            snapshot.Id,
            snapshot.SprintId,
            snapshot.SprintName,
            snapshot.Goal,
            snapshot.StartedAt,
            snapshot.CompletedAt,
            snapshot.CapacityPoints,
            snapshot.CommittedItemCount,
            snapshot.CompletedItemCount,
            snapshot.CommittedPoints,
            snapshot.CompletedPoints,
            snapshot.Items.Select(item => new Wire.WorkSprintSnapshotItem(
                item.ItemId,
                item.Kind,
                item.Title,
                item.Status,
                item.EstimatePoints,
                item.Completed)).ToList())).ToList(),
        report.Burndown.Select(series => new Wire.WorkSprintBurndownSeries(
            series.SprintId,
            series.SprintName,
            series.Status,
            series.CapacityPoints,
            series.Points.Select(point => new Wire.WorkSprintMetricPoint(
                point.Id,
                point.OccurredAt,
                point.Reason,
                point.ScopeItemCount,
                point.CompletedItemCount,
                point.ScopePoints,
                point.CompletedPoints,
                point.RemainingPoints)).ToList())).ToList(),
        report.ActiveForecast is null
            ? null
            : new Wire.WorkSprintForecast(
                report.ActiveForecast.SprintId,
                report.ActiveForecast.SprintName,
                report.ActiveForecast.RemainingPoints,
                report.ActiveForecast.AverageVelocity,
                report.ActiveForecast.ProjectedSprintsRequired,
                report.ActiveForecast.IsOverCapacity));

    private static Guid? ParseGuid(string? value) =>
        Guid.TryParse(value, out var parsed) ? parsed : null;

    private static CapabilityResult Success<T>(string requestId, T payload) => new()
    {
        RequestId = requestId,
        Succeeded = true,
        ContentType = "application/json",
        Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions))
    };

    private static CapabilityResult Failure(
        string requestId,
        PlatformCapabilityErrorCode code,
        string message) => new()
        {
            RequestId = requestId,
            Succeeded = false,
            ContentType = "application/json",
            Error = message,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(
                new PlatformCapabilityError(code, message), JsonOptions))
        };

}
