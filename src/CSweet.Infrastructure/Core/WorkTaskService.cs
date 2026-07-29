using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed class WorkTaskService : IWorkTaskService
{
    private readonly CSweetDbContext _dbContext;
    private readonly IAuditEventWriter _auditEventWriter;

    public WorkTaskService(CSweetDbContext dbContext, IAuditEventWriter auditEventWriter)
    {
        _dbContext = dbContext;
        _auditEventWriter = auditEventWriter;
    }

    public async Task<IReadOnlyList<WorkTaskResponse>> ListByOrganizationAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.CoreWorkTasks
            .Where(x => x.OrganizationId == organizationId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.ToResponse())
            .ToListAsync(cancellationToken);
    }

    public async Task<WorkTaskResponse?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var task = await _dbContext.CoreWorkTasks
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        return task?.ToResponse();
    }

    public async Task<CoreActionResponse> CreateAsync(Guid organizationId, CreateWorkTaskRequest request, CancellationToken cancellationToken = default)
    {
        if (!await _dbContext.CoreOrganizations.AnyAsync(x => x.Id == organizationId, cancellationToken))
        {
            return Failure("organization_not_found", "Organization was not found.");
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return Failure("validation_error", "Task title is required.");
        }
        if (!Enum.IsDefined(typeof(WorkTaskStatus), request.Status) ||
            !Enum.IsDefined(typeof(WorkTaskPriority), request.Priority))
            return Failure("validation_error", "Task status or priority is invalid.");

        // Validate strategic objective belongs to organization if provided
        if (request.StrategicObjectiveId.HasValue)
        {
            var objExists = await _dbContext.CoreStrategicObjectives
                .AnyAsync(x => x.Id == request.StrategicObjectiveId && x.OrganizationId == organizationId, cancellationToken);

            if (!objExists)
            {
                return Failure("invalid_objective", "Strategic objective does not belong to this organization.");
            }
        }

        // Validate assigned role belongs to organization if provided
        if (request.AssignedRoleId.HasValue)
        {
            var roleExists = await _dbContext.CoreRoles
                .AnyAsync(x => x.Id == request.AssignedRoleId && x.OrganizationId == organizationId, cancellationToken);

            if (!roleExists)
            {
                return Failure("invalid_role", "Assigned role does not belong to this organization.");
            }
        }

        // Validate assigned worker is global or belongs to organization if provided
        if (request.AssignedWorkerId.HasValue)
        {
            var workerExists = await _dbContext.CoreWorkers
                .AnyAsync(x => x.Id == request.AssignedWorkerId && (x.OrganizationId == organizationId || x.OrganizationId == null), cancellationToken);

            if (!workerExists)
            {
                return Failure("invalid_worker", "Assigned worker does not belong to this organization.");
            }
        }

        var now = DateTimeOffset.UtcNow;
        CSweet.Domain.WorkManagement.WorkBoard board;
        if (request.BoardId.HasValue)
        {
            var requestedBoard = await _dbContext.WorkBoards
                .Include(x => x.Columns)
                .SingleOrDefaultAsync(x =>
                x.Id == request.BoardId &&
                x.OrganizationId == organizationId &&
                x.ArchivedAt == null, cancellationToken);
            if (requestedBoard is null)
                return Failure("invalid_board", "Board does not belong to this organization or is archived.");
            board = requestedBoard;
        }
        else
        {
            board = await WorkBoardProvisioning.EnsureDefaultBoardAsync(
                _dbContext, organizationId, cancellationToken);
        }
        var status = (WorkTaskStatus)request.Status;
        var column = board.Columns.OrderBy(x => x.Position).FirstOrDefault(x =>
            status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled
                ? x.Category == CSweet.Domain.WorkManagement.WorkBoardColumnCategory.Done
                : status is WorkTaskStatus.Assigned or WorkTaskStatus.Running or WorkTaskStatus.WaitingForApproval
                    ? x.Category == CSweet.Domain.WorkManagement.WorkBoardColumnCategory.InProgress
                    : x.Category == CSweet.Domain.WorkManagement.WorkBoardColumnCategory.ToDo)
            ?? board.Columns.OrderBy(x => x.Position).First();
        var nextRank = (await _dbContext.CoreWorkTasks
            .Where(x => x.BoardColumnId == column.Id)
            .MaxAsync(x => (long?)x.BoardRank, cancellationToken) ?? 0) + 1024;
        if (column.WipPolicy == CSweet.Domain.WorkManagement.WorkBoardWipPolicy.HardLimit &&
            column.WipLimit.HasValue &&
            await _dbContext.CoreWorkTasks.CountAsync(
                x => x.BoardColumnId == column.Id, cancellationToken) >= column.WipLimit.Value)
            return Failure("wip_limit", $"Column '{column.Name}' has reached its WIP limit.");
        var task = new WorkTask
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            BoardId = board.Id,
            BoardColumnId = column.Id,
            BoardRank = nextRank,
            StrategicObjectiveId = request.StrategicObjectiveId,
            AssignedRoleId = request.AssignedRoleId,
            AssignedWorkerId = request.AssignedWorkerId,
            Title = request.Title.Trim(),
            Description = request.Description ?? string.Empty,
            Status = status,
            Priority = (WorkTaskPriority)request.Priority,
            DueDate = request.DueDate,
            RequiresApproval = request.RequiresApproval,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.CoreWorkTasks.Add(task);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditEventWriter.WriteAsync(
            "task.created",
            "WorkTask",
            task.Id,
            $"Task '{task.Title}' created.",
            cancellationToken: cancellationToken);

        return new CoreActionResponse(true, null, "Task created successfully.", WorkTask: task.ToResponse());
    }

    public async Task<CoreActionResponse> UpdateAsync(Guid id, UpdateWorkTaskRequest request, CancellationToken cancellationToken = default)
    {
        var task = await _dbContext.CoreWorkTasks
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (task is null)
        {
            return Failure("not_found", "Task was not found.");
        }
        if (request.Status.HasValue &&
            !Enum.IsDefined(typeof(WorkTaskStatus), request.Status.Value))
            return Failure("validation_error", "Task status is invalid.");
        if (request.Priority.HasValue &&
            !Enum.IsDefined(typeof(WorkTaskPriority), request.Priority.Value))
            return Failure("validation_error", "Task priority is invalid.");

        if (!string.IsNullOrWhiteSpace(request.Title))
            task.Title = request.Title.Trim();
        if (!string.IsNullOrEmpty(request.Description))
            task.Description = request.Description;
        if (request.StrategicObjectiveId.HasValue)
            task.StrategicObjectiveId = request.StrategicObjectiveId;
        if (request.AssignedRoleId.HasValue)
            task.AssignedRoleId = request.AssignedRoleId;
        if (request.AssignedWorkerId.HasValue)
            task.AssignedWorkerId = request.AssignedWorkerId;
        if (request.Status.HasValue && request.Status.Value != (int)task.Status)
        {
            var status = (WorkTaskStatus)request.Status.Value;
            var columns = await _dbContext.WorkBoardColumns
                .Where(x => x.BoardId == task.BoardId)
                .OrderBy(x => x.Position)
                .ToListAsync(cancellationToken);
            var category = status switch
            {
                WorkTaskStatus.Completed => CSweet.Domain.WorkManagement.WorkBoardColumnCategory.Done,
                WorkTaskStatus.Cancelled => CSweet.Domain.WorkManagement.WorkBoardColumnCategory.Cancelled,
                WorkTaskStatus.Assigned or WorkTaskStatus.Running or WorkTaskStatus.WaitingForApproval =>
                    CSweet.Domain.WorkManagement.WorkBoardColumnCategory.InProgress,
                _ => CSweet.Domain.WorkManagement.WorkBoardColumnCategory.ToDo
            };
            var column = columns.FirstOrDefault(x => x.Category == category)
                ?? columns.FirstOrDefault(x => x.Category ==
                    (category == CSweet.Domain.WorkManagement.WorkBoardColumnCategory.Cancelled
                        ? CSweet.Domain.WorkManagement.WorkBoardColumnCategory.Done
                        : CSweet.Domain.WorkManagement.WorkBoardColumnCategory.ToDo));
            if (column is not null)
            {
                if (column.WipPolicy == CSweet.Domain.WorkManagement.WorkBoardWipPolicy.HardLimit &&
                    column.WipLimit.HasValue &&
                    await _dbContext.CoreWorkTasks.CountAsync(
                        x => x.BoardColumnId == column.Id && x.Id != task.Id,
                        cancellationToken) >= column.WipLimit.Value)
                    return Failure("wip_limit", $"Column '{column.Name}' has reached its WIP limit.");
                task.BoardColumnId = column.Id;
                task.BoardRank = (await _dbContext.CoreWorkTasks
                    .Where(x => x.BoardColumnId == column.Id && x.Id != task.Id)
                    .MaxAsync(x => (long?)x.BoardRank, cancellationToken) ?? 0) + 1024;
            }
            task.Status = status;
        }
        if (request.Priority.HasValue)
            task.Priority = (WorkTaskPriority)request.Priority.Value;
        if (request.DueDate.HasValue)
            task.DueDate = request.DueDate.Value;
        if (request.RequiresApproval.HasValue)
            task.RequiresApproval = request.RequiresApproval.Value;

        task.UpdatedAt = DateTimeOffset.UtcNow;
        task.Revision++;
        await WorkSprintMetricsRecorder.RecordAsync(
            _dbContext, task.SprintId, "item.updated",
            task.UpdatedAt, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditEventWriter.WriteAsync(
            "task.updated",
            "WorkTask",
            task.Id,
            $"Task '{task.Title}' updated.",
            cancellationToken: cancellationToken);

        return new CoreActionResponse(true, null, "Task updated successfully.", WorkTask: task.ToResponse());
    }

    public async Task<CoreActionResponse> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var task = await _dbContext.CoreWorkTasks
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (task is null)
        {
            return Failure("not_found", "Task was not found.");
        }

        var title = task.Title;
        _dbContext.CoreWorkTasks.Remove(task);
        await WorkSprintMetricsRecorder.RecordAsync(
            _dbContext, task.SprintId, "item.deleted",
            DateTimeOffset.UtcNow, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditEventWriter.WriteAsync(
            "task.deleted",
            "WorkTask",
            task.Id,
            $"Task '{title}' deleted.",
            cancellationToken: cancellationToken);

        return new CoreActionResponse(true, null, "Task deleted successfully.");
    }

    static CoreActionResponse Failure(string errorCode, string message) =>
        new CoreActionResponse(false, errorCode, message);
}
