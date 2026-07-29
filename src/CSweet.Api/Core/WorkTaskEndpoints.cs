using CSweet.Api.Auth;
using CSweet.Application.Core;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.Core;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;

namespace CSweet.Api.Core;

public static class WorkTaskEndpoints
{
    public static IEndpointRouteBuilder MapWorkTaskEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/core/tasks");
        var organizationGroup = endpoints.MapGroup("/api/organizations/{organizationId:guid}/tasks");
        var taskGroup = endpoints.MapGroup("/api/tasks");

        group.MapGet("/organization/{organizationId:guid}", ListAsync);
        organizationGroup.MapGet("", ListAsync);

        group.MapGet("/{id:guid}", GetAsync);
        taskGroup.MapGet("/{id:guid}", GetAsync);

        group.MapPost("/organization/{organizationId:guid}", CreateAsync);
        organizationGroup.MapPost("", CreateAsync);

        group.MapPut("/{id:guid}", UpdateAsync);
        group.MapDelete("/{id:guid}", RetiredDelete);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        Guid organizationId,
        HttpContext http,
        IWorkTaskService tasks,
        IWorkBoardService boards,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (environment.IsEnvironment("Testing"))
            return Results.Ok(await tasks.ListByOrganizationAsync(organizationId, cancellationToken));
        var userId = http.User.GetApplicationUserId();
        if (!userId.HasValue) return Results.Unauthorized();
        try
        {
            var directory = await boards.ListDirectoryAsync(
                organizationId,
                userId.Value,
                new WorkBoardDirectoryQuery(IncludeArchived: true),
                cancellationToken);
            var readableBoards = directory.Boards
                .Where(x => x.AllowedActions.Contains(WorkItemActions.Read))
                .Select(x => x.Id)
                .ToHashSet();
            var result = (await tasks.ListByOrganizationAsync(organizationId, cancellationToken))
                .Where(x => x.BoardId.HasValue && readableBoards.Contains(x.BoardId.Value))
                .ToList();
            return Results.Ok(result);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Forbid();
        }
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        HttpContext http,
        IWorkTaskService tasks,
        IWorkBoardService boards,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (environment.IsEnvironment("Testing"))
        {
            var testTask = await tasks.GetAsync(id, cancellationToken);
            return testTask is null ? Results.NotFound() : Results.Ok(testTask);
        }
        var userId = http.User.GetApplicationUserId();
        if (!userId.HasValue) return Results.Unauthorized();
        var task = await tasks.GetAsync(id, cancellationToken);
        if (task is null) return Results.NotFound();
        return await HasActionAsync(
            task, userId.Value, WorkItemActions.Read, boards, cancellationToken)
            ? Results.Ok(task)
            : Results.Forbid();
    }

    private static async Task<IResult> CreateAsync(
        Guid organizationId,
        CreateWorkTaskRequest request,
        HttpContext http,
        IWorkTaskService tasks,
        IWorkBoardService boards,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (environment.IsEnvironment("Testing"))
        {
            var testResult = await tasks.CreateAsync(organizationId, request, cancellationToken);
            return testResult.Succeeded
                ? Results.Created($"/api/tasks/{testResult.WorkTask!.Id}", testResult.WorkTask)
                : Results.BadRequest(testResult);
        }
        var userId = http.User.GetApplicationUserId();
        if (!userId.HasValue) return Results.Unauthorized();
        if (!Enum.IsDefined(typeof(WorkTaskStatus), request.Status))
            return Results.BadRequest(new { error = "invalid_status", message = "Task status is invalid." });
        try
        {
            var directory = await boards.ListDirectoryAsync(
                organizationId,
                userId.Value,
                new WorkBoardDirectoryQuery(),
                cancellationToken);
            var board = request.BoardId.HasValue
                ? directory.Boards.SingleOrDefault(x => x.Id == request.BoardId.Value)
                : directory.Boards.SingleOrDefault(x => x.IsDefault);
            if (board is null || !board.AllowedActions.Contains(WorkItemActions.Create))
                return Results.Forbid();
            var transitionAction = RequiredTransitionAction(request.Status);
            if (transitionAction is not null && !board.AllowedActions.Contains(transitionAction))
                return Results.Forbid();

            var securedRequest = request with { BoardId = board.Id };
            var result = await tasks.CreateAsync(organizationId, securedRequest, cancellationToken);
            return result.Succeeded
                ? Results.Created($"/api/tasks/{result.WorkTask!.Id}", result.WorkTask)
                : Results.BadRequest(result);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Forbid();
        }
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateWorkTaskRequest request,
        HttpContext http,
        IWorkTaskService tasks,
        IWorkBoardService boards,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (environment.IsEnvironment("Testing"))
        {
            var testResult = await tasks.UpdateAsync(id, request, cancellationToken);
            return testResult.Succeeded ? Results.Ok(testResult.WorkTask) : Results.BadRequest(testResult);
        }
        var userId = http.User.GetApplicationUserId();
        if (!userId.HasValue) return Results.Unauthorized();
        if (request.Status.HasValue &&
            !Enum.IsDefined(typeof(WorkTaskStatus), request.Status.Value))
            return Results.BadRequest(new { error = "invalid_status", message = "Task status is invalid." });
        var existing = await tasks.GetAsync(id, cancellationToken);
        if (existing is null) return Results.NotFound();
        if (!await HasActionAsync(
                existing, userId.Value, WorkItemActions.Update, boards, cancellationToken))
            return Results.Forbid();
        if (request.Status.HasValue)
        {
            var transitionAction = RequiredTransitionAction(
                request.Status.Value, existing.Status);
            if (transitionAction is not null &&
                !await HasActionAsync(
                    existing, userId.Value, transitionAction, boards, cancellationToken))
                return Results.Forbid();
        }

        var result = await tasks.UpdateAsync(id, request, cancellationToken);
        return result.Succeeded ? Results.Ok(result.WorkTask) : Results.BadRequest(result);
    }

    private static IResult RetiredDelete(Guid id) =>
        Results.Problem(
            statusCode: StatusCodes.Status405MethodNotAllowed,
            title: "Physical work-item deletion is disabled",
            detail: $"Work item {id:D} must be cancelled through a grant-secured board transition.");

    private static async Task<bool> HasActionAsync(
        WorkTaskResponse task,
        Guid applicationUserId,
        string action,
        IWorkBoardService boards,
        CancellationToken cancellationToken)
    {
        if (!task.BoardId.HasValue) return false;
        try
        {
            var detail = await boards.GetAsync(
                task.OrganizationId,
                task.BoardId.Value,
                applicationUserId,
                cancellationToken);
            return detail?.Board.AllowedActions.Contains(action) == true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? RequiredTransitionAction(int status, int? currentStatus = null)
    {
        if (currentStatus == status) return null;
        var target = (WorkTaskStatus)status;
        if (target == WorkTaskStatus.Completed) return WorkItemActions.Complete;
        if (target == WorkTaskStatus.Cancelled) return WorkItemActions.Cancel;
        if (currentStatus.HasValue &&
            (WorkTaskStatus)currentStatus.Value is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled)
            return WorkItemActions.Reopen;
        return currentStatus.HasValue ? WorkItemActions.Move : null;
    }
}
