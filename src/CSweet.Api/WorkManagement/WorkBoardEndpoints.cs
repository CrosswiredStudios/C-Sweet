using CSweet.Api.Auth;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.WorkManagement;

public static class WorkBoardEndpoints
{
    public static IEndpointRouteBuilder MapWorkBoardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/work/boards");
        var organizationGrantGroup =
            endpoints.MapGroup("/api/organizations/{organizationId:guid}/work/grants");

        organizationGrantGroup.MapGet("", async (
            Guid organizationId, HttpContext http,
            IWorkBoardGrantService grants, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                return Results.Ok(await grants.ListOrganizationAsync(
                    organizationId, userId.Value, cancellationToken));
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });

        organizationGrantGroup.MapPut("", async (
            Guid organizationId, SetWorkBoardSubjectGrantsRequest request,
            HttpContext http, IWorkBoardGrantService grants, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                return Results.Ok(await grants.SetOrganizationSubjectGrantsAsync(
                    organizationId, userId.Value, request, cancellationToken));
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = "invalid_grant", message = exception.Message });
            }
        });

        group.MapGet("", async (
            Guid organizationId, string? search, Guid? workstreamId,
            bool? includeArchived, bool? favoritesOnly,
            HttpContext http, IWorkBoardService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                return Results.Ok(await service.ListDirectoryAsync(
                    organizationId, userId.Value,
                    new WorkBoardDirectoryQuery(
                        search, workstreamId, includeArchived ?? false, favoritesOnly ?? false),
                    cancellationToken));
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });

        group.MapGet("/{boardId:guid}", async (
            Guid organizationId, Guid boardId, HttpContext http,
            IWorkBoardService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                var result = await service.GetAsync(
                    organizationId, boardId, userId.Value, cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });

        group.MapPost("", async (
            Guid organizationId, CreateWorkBoardRequest request, HttpContext http,
            IWorkBoardService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                var result = await service.CreateAsync(
                    organizationId, userId.Value, request, cancellationToken);
                return Results.Created(
                    $"/api/organizations/{organizationId:D}/work/boards/{result.Board.Id:D}", result);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = "invalid_board", message = exception.Message });
            }
        });

        group.MapPut("/{boardId:guid}", async (
            Guid organizationId, Guid boardId, UpdateWorkBoardRequest request, HttpContext http,
            IWorkBoardService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                var result = await service.UpdateAsync(
                    organizationId, boardId, userId.Value, request, cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = "invalid_board", message = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = "board_conflict", message = exception.Message });
            }
            catch (DbUpdateConcurrencyException exception)
            {
                return Results.Conflict(new { error = "revision_conflict", message = exception.Message });
            }
        });

        group.MapPost("/{boardId:guid}/archive", (
            Guid organizationId, Guid boardId, HttpContext http,
            IWorkBoardService service, CancellationToken cancellationToken) =>
            ChangeArchiveStateAsync(organizationId, boardId, true, http, service, cancellationToken));

        group.MapPost("/{boardId:guid}/restore", (
            Guid organizationId, Guid boardId, HttpContext http,
            IWorkBoardService service, CancellationToken cancellationToken) =>
            ChangeArchiveStateAsync(organizationId, boardId, false, http, service, cancellationToken));

        group.MapPut("/{boardId:guid}/favorite", async (
            Guid organizationId, Guid boardId, SetWorkBoardFavoriteRequest request,
            HttpContext http, IWorkBoardService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                return await service.SetFavoriteAsync(
                    organizationId, boardId, userId.Value, request.IsFavorite, cancellationToken)
                    ? Results.NoContent()
                    : Results.NotFound();
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });

        group.MapPut("/{boardId:guid}/columns", async (
            Guid organizationId, Guid boardId, ConfigureWorkBoardColumnsRequest request,
            HttpContext http, IWorkBoardService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                var result = await service.ConfigureColumnsAsync(
                    organizationId, boardId, userId.Value, request, cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = "invalid_columns", message = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = "column_conflict", message = exception.Message });
            }
            catch (DbUpdateConcurrencyException exception)
            {
                return Results.Conflict(new { error = "revision_conflict", message = exception.Message });
            }
        });

        group.MapPost("/{boardId:guid}/items", async (
            Guid organizationId, Guid boardId, CreateBoardWorkItemRequest request,
            HttpContext http, IWorkBoardService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                var result = await service.CreateItemAsync(
                    organizationId, boardId, userId.Value, request, cancellationToken);
                return Results.Created(
                    $"/api/organizations/{organizationId:D}/work/boards/{boardId:D}/items/{result.Id:D}",
                    result);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = "invalid_work_item", message = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = "work_item_conflict", message = exception.Message });
            }
        });

        group.MapPost("/{boardId:guid}/items/{itemId:guid}/move", async (
            Guid organizationId, Guid boardId, Guid itemId, MoveBoardWorkItemRequest request,
            HttpContext http, IWorkBoardService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                var result = await service.MoveItemAsync(
                    organizationId, boardId, itemId, userId.Value, request, cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = "invalid_move", message = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = "wip_limit", message = exception.Message });
            }
            catch (DbUpdateConcurrencyException exception)
            {
                return Results.Conflict(new { error = "revision_conflict", message = exception.Message });
            }
        });

        group.MapGet("/{boardId:guid}/items/{itemId:guid}/collaboration", async (
            Guid organizationId, Guid boardId, Guid itemId, HttpContext http,
            IWorkItemCollaborationService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                var result = await service.GetAsync(
                    organizationId, boardId, itemId, userId.Value, cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });

        group.MapPost("/{boardId:guid}/items/{itemId:guid}/comments", async (
            Guid organizationId, Guid boardId, Guid itemId, AddWorkItemCommentRequest request,
            HttpContext http, IWorkItemCollaborationService service,
            CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                var result = await service.AddCommentAsync(
                    organizationId, boardId, itemId, userId.Value, request, cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = "invalid_comment", message = exception.Message });
            }
        });

        group.MapPost("/{boardId:guid}/items/{itemId:guid}/transfer", async (
            Guid organizationId, Guid boardId, Guid itemId, TransferWorkItemRequest request,
            HttpContext http, IWorkItemCollaborationService service,
            CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                var result = await service.TransferAsync(
                    organizationId, boardId, itemId, userId.Value, request, cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = "invalid_transfer", message = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = "transfer_conflict", message = exception.Message });
            }
            catch (DbUpdateConcurrencyException exception)
            {
                return Results.Conflict(new { error = "revision_conflict", message = exception.Message });
            }
        });

        group.MapGet("/{boardId:guid}/sprints", async (
            Guid organizationId, Guid boardId, HttpContext http,
            IWorkSprintService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                return Results.Ok(await service.ListAsync(
                    organizationId, boardId, userId.Value, cancellationToken));
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        group.MapPost("/{boardId:guid}/sprints", async (
            Guid organizationId, Guid boardId, CreateWorkSprintRequest request,
            HttpContext http, IWorkSprintService service,
            CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                var result = await service.CreateAsync(
                    organizationId, boardId, userId.Value, request, cancellationToken);
                return Results.Created(
                    $"/api/organizations/{organizationId:D}/work/boards/{boardId:D}/sprints/{result.Id:D}",
                    result);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = "invalid_sprint", message = exception.Message });
            }
        });

        group.MapPost("/{boardId:guid}/sprints/{sprintId:guid}/start", (
            Guid organizationId, Guid boardId, Guid sprintId,
            ChangeWorkSprintStateRequest request, HttpContext http,
            IWorkSprintService service, CancellationToken cancellationToken) =>
            ChangeSprintStateAsync(
                organizationId, boardId, sprintId, WorkSprintActions.Start,
                request, http, service, cancellationToken));

        group.MapPost("/{boardId:guid}/sprints/{sprintId:guid}/complete", (
            Guid organizationId, Guid boardId, Guid sprintId,
            ChangeWorkSprintStateRequest request, HttpContext http,
            IWorkSprintService service, CancellationToken cancellationToken) =>
            ChangeSprintStateAsync(
                organizationId, boardId, sprintId, WorkSprintActions.Complete,
                request, http, service, cancellationToken));

        group.MapPost("/{boardId:guid}/sprints/{sprintId:guid}/cancel", (
            Guid organizationId, Guid boardId, Guid sprintId,
            ChangeWorkSprintStateRequest request, HttpContext http,
            IWorkSprintService service, CancellationToken cancellationToken) =>
            ChangeSprintStateAsync(
                organizationId, boardId, sprintId, WorkSprintActions.Cancel,
                request, http, service, cancellationToken));

        group.MapPut("/{boardId:guid}/items/{itemId:guid}/sprint", async (
            Guid organizationId, Guid boardId, Guid itemId,
            SetWorkItemSprintRequest request, HttpContext http,
            IWorkSprintService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                var result = await service.SetItemSprintAsync(
                    organizationId, boardId, itemId, userId.Value,
                    request, cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = "invalid_sprint_scope", message = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = "sprint_scope_conflict", message = exception.Message });
            }
            catch (DbUpdateConcurrencyException exception)
            {
                return Results.Conflict(new { error = "revision_conflict", message = exception.Message });
            }
        });

        group.MapPut("/{boardId:guid}/items/{itemId:guid}/estimate", async (
            Guid organizationId, Guid boardId, Guid itemId,
            SetWorkItemEstimateRequest request, HttpContext http,
            IWorkSprintService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                var result = await service.SetItemEstimateAsync(
                    organizationId, boardId, itemId, userId.Value,
                    request, cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = "invalid_estimate", message = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = "estimate_conflict", message = exception.Message });
            }
            catch (DbUpdateConcurrencyException exception)
            {
                return Results.Conflict(new { error = "revision_conflict", message = exception.Message });
            }
        });

        group.MapPut("/{boardId:guid}/sprints/{sprintId:guid}/capacity", async (
            Guid organizationId, Guid boardId, Guid sprintId,
            SetWorkSprintCapacityRequest request, HttpContext http,
            IWorkSprintService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                var result = await service.SetCapacityAsync(
                    organizationId, boardId, sprintId, userId.Value,
                    request, cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = "invalid_capacity", message = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = "capacity_conflict", message = exception.Message });
            }
            catch (DbUpdateConcurrencyException exception)
            {
                return Results.Conflict(new { error = "revision_conflict", message = exception.Message });
            }
        });

        group.MapPost("/{boardId:guid}/sprints/{sprintId:guid}/carryover", async (
            Guid organizationId, Guid boardId, Guid sprintId,
            CarryOverSprintRequest request, HttpContext http,
            IWorkSprintService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                var result = await service.CarryOverAsync(
                    organizationId, boardId, sprintId, userId.Value,
                    request, cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = "invalid_carryover", message = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = "carryover_conflict", message = exception.Message });
            }
            catch (DbUpdateConcurrencyException exception)
            {
                return Results.Conflict(new { error = "revision_conflict", message = exception.Message });
            }
        });

        group.MapGet("/{boardId:guid}/sprint-report", async (
            Guid organizationId, Guid boardId, HttpContext http,
            IWorkSprintService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                return Results.Ok(await service.GetReportAsync(
                    organizationId, boardId, userId.Value, cancellationToken));
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        group.MapGet("/{boardId:guid}/automations", async (
            Guid organizationId, Guid boardId, HttpContext http,
            IWorkAutomationService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                return Results.Ok(await service.ListAsync(
                    organizationId, boardId, userId.Value, cancellationToken));
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        group.MapPost("/{boardId:guid}/automations", async (
            Guid organizationId, Guid boardId,
            CreateWorkAutomationRuleRequest request, HttpContext http,
            IWorkAutomationService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                var result = await service.CreateAsync(
                    organizationId, boardId, userId.Value, request, cancellationToken);
                return Results.Created(
                    $"/api/organizations/{organizationId:D}/work/boards/{boardId:D}/automations/{result.Id:D}",
                    result);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(
                    new { error = "invalid_automation", message = exception.Message });
            }
        });

        group.MapPut("/{boardId:guid}/automations/{ruleId:guid}", async (
            Guid organizationId, Guid boardId, Guid ruleId,
            UpdateWorkAutomationRuleRequest request, HttpContext http,
            IWorkAutomationService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                var result = await service.UpdateAsync(
                    organizationId, boardId, ruleId, userId.Value,
                    request, cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(
                    new { error = "invalid_automation", message = exception.Message });
            }
            catch (DbUpdateConcurrencyException exception)
            {
                return Results.Conflict(
                    new { error = "revision_conflict", message = exception.Message });
            }
        });

        group.MapDelete("/{boardId:guid}/automations/{ruleId:guid}", async (
            Guid organizationId, Guid boardId, Guid ruleId,
            long expectedRevision, HttpContext http,
            IWorkAutomationService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                return await service.DeleteAsync(
                    organizationId, boardId, ruleId, userId.Value,
                    expectedRevision, cancellationToken)
                    ? Results.NoContent()
                    : Results.NotFound();
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(
                    new { error = "automation_history_exists", message = exception.Message });
            }
            catch (DbUpdateConcurrencyException exception)
            {
                return Results.Conflict(
                    new { error = "revision_conflict", message = exception.Message });
            }
        });

        group.MapGet("/{boardId:guid}/grants", async (
            Guid organizationId, Guid boardId, HttpContext http,
            IWorkBoardGrantService grants, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                return Results.Ok(await grants.ListAsync(
                    organizationId, boardId, userId.Value, cancellationToken));
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        group.MapPut("/{boardId:guid}/grants", async (
            Guid organizationId, Guid boardId, SetWorkBoardSubjectGrantsRequest request,
            HttpContext http, IWorkBoardGrantService grants, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                return Results.Ok(await grants.SetSubjectGrantsAsync(
                    organizationId, boardId, userId.Value, request, cancellationToken));
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = "invalid_grant", message = exception.Message });
            }
        });

        return endpoints;
    }

    private static async Task<IResult> ChangeSprintStateAsync(
        Guid organizationId,
        Guid boardId,
        Guid sprintId,
        string action,
        ChangeWorkSprintStateRequest request,
        HttpContext http,
        IWorkSprintService service,
        CancellationToken cancellationToken)
    {
        var userId = http.User.GetApplicationUserId();
        if (!userId.HasValue) return Results.Unauthorized();
        try
        {
            var result = await service.ChangeStateAsync(
                organizationId, boardId, sprintId, userId.Value,
                action, request, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = "invalid_sprint_state", message = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new { error = "sprint_state_conflict", message = exception.Message });
        }
        catch (DbUpdateConcurrencyException exception)
        {
            return Results.Conflict(new { error = "revision_conflict", message = exception.Message });
        }
    }

    private static async Task<IResult> ChangeArchiveStateAsync(
        Guid organizationId, Guid boardId, bool archive, HttpContext http,
        IWorkBoardService service, CancellationToken cancellationToken)
    {
        var userId = http.User.GetApplicationUserId();
        if (!userId.HasValue) return Results.Unauthorized();
        try
        {
            var changed = archive
                ? await service.ArchiveAsync(
                    organizationId, boardId, userId.Value, cancellationToken)
                : await service.RestoreAsync(
                    organizationId, boardId, userId.Value, cancellationToken);
            return changed ? Results.NoContent() : Results.NotFound();
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new { error = "board_conflict", message = exception.Message });
        }
    }
}
