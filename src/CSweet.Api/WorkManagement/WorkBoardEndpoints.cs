using CSweet.Api.Auth;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.WorkManagement;
using CSweet.Infrastructure.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.Api.WorkManagement;

public static class WorkBoardEndpoints
{
    public static IEndpointRouteBuilder MapWorkBoardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/work/boards");
        var organizationGrantGroup =
            endpoints.MapGroup("/api/organizations/{organizationId:guid}/work/grants");
        var repositoryGroup =
            endpoints.MapGroup("/api/organizations/{organizationId:guid}/source-control/repositories");
        var personalTodoGroup =
            endpoints.MapGroup("/api/organizations/{organizationId:guid}/work/personal-todos");
        var orchestrationGroup =
            endpoints.MapGroup(
                "/api/organizations/{organizationId:guid}/work/boards/{boardId:guid}/orchestration");

        personalTodoGroup.MapGet("/", async (
            Guid organizationId, bool? includeArchived, HttpContext http, CSweetDbContext db,
            IPersonalTodoService service, CancellationToken cancellationToken) =>
        {
            var actor = await ResolvePersonalTodoActorAsync(
                organizationId, http, db, cancellationToken);
            if (actor is null) return Results.Unauthorized();
            try { return Results.Ok(await service.ListAsync(organizationId, actor, includeArchived ?? false, cancellationToken)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });

        personalTodoGroup.MapPost("/items", async (
            Guid organizationId, Wire.AddPersonalTodoItemRequest request,
            HttpContext http, CSweetDbContext db, IPersonalTodoService service,
            CancellationToken cancellationToken) =>
        {
            var actor = await ResolvePersonalTodoActorAsync(
                organizationId, http, db, cancellationToken);
            if (actor is null) return Results.Unauthorized();
            try { return Results.Ok(await service.AddAsync(organizationId, actor, request, cancellationToken)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException exception)
            { return Results.BadRequest(new { error = "invalid_personal_todo", message = exception.Message }); }
        });

        personalTodoGroup.MapPost("/items/reorder", async (
            Guid organizationId, Wire.ReorderPersonalTodoItemRequest request,
            HttpContext http, CSweetDbContext db, IPersonalTodoService service,
            CancellationToken cancellationToken) =>
        {
            var actor = await ResolvePersonalTodoActorAsync(
                organizationId, http, db, cancellationToken);
            if (actor is null) return Results.Unauthorized();
            try { return Results.Ok(await service.ReorderAsync(organizationId, actor, request, cancellationToken)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException exception)
            { return Results.Conflict(new { error = "revision_conflict", message = exception.Message }); }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            { return Results.BadRequest(new { error = "invalid_personal_todo", message = exception.Message }); }
        });

        personalTodoGroup.MapPost("/items/requeue", async (
            Guid organizationId, Wire.RequeuePersonalTodoItemRequest request,
            HttpContext http, CSweetDbContext db, IPersonalTodoService service,
            CancellationToken cancellationToken) =>
        {
            var actor = await ResolvePersonalTodoActorAsync(
                organizationId, http, db, cancellationToken);
            if (actor is null) return Results.Unauthorized();
            try { return Results.Ok(await service.RequeueAsync(organizationId, actor, request, cancellationToken)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException exception)
            { return Results.Conflict(new { error = "revision_conflict", message = exception.Message }); }
            catch (InvalidOperationException exception)
            { return Results.BadRequest(new { error = "invalid_personal_todo", message = exception.Message }); }
        });

        personalTodoGroup.MapPut("/items", async (
            Guid organizationId, Wire.UpdatePersonalTodoItemRequest request,
            HttpContext http, CSweetDbContext db, IPersonalTodoService service,
            CancellationToken cancellationToken) =>
        {
            var actor = await ResolvePersonalTodoActorAsync(organizationId, http, db, cancellationToken);
            if (actor is null) return Results.Unauthorized();
            try { return Results.Ok(await service.UpdateAsync(organizationId, actor, request, cancellationToken)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException exception)
            { return Results.Conflict(new { error = "revision_conflict", message = exception.Message }); }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            { return Results.BadRequest(new { error = "invalid_personal_task", message = exception.Message }); }
        });

        personalTodoGroup.MapPost("/items/archive", async (
            Guid organizationId, Wire.ArchivePersonalTodoItemRequest request,
            HttpContext http, CSweetDbContext db, IPersonalTodoService service,
            CancellationToken cancellationToken) =>
        {
            var actor = await ResolvePersonalTodoActorAsync(organizationId, http, db, cancellationToken);
            if (actor is null) return Results.Unauthorized();
            try { return Results.Ok(await service.ArchiveAsync(organizationId, actor, request, cancellationToken)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException exception)
            { return Results.Conflict(new { error = "revision_conflict", message = exception.Message }); }
        });

        personalTodoGroup.MapPost("/items/restore", async (
            Guid organizationId, Wire.RestorePersonalTodoItemRequest request,
            HttpContext http, CSweetDbContext db, IPersonalTodoService service,
            CancellationToken cancellationToken) =>
        {
            var actor = await ResolvePersonalTodoActorAsync(organizationId, http, db, cancellationToken);
            if (actor is null) return Results.Unauthorized();
            try { return Results.Ok(await service.RestoreAsync(organizationId, actor, request, cancellationToken)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException exception)
            { return Results.Conflict(new { error = "revision_conflict", message = exception.Message }); }
        });

        personalTodoGroup.MapPost("/items/status", async (
            Guid organizationId, Wire.SetHumanPersonalTodoStatusRequest request,
            HttpContext http, CSweetDbContext db, IPersonalTodoService service,
            CancellationToken cancellationToken) =>
        {
            var actor = await ResolvePersonalTodoActorAsync(organizationId, http, db, cancellationToken);
            if (actor is null) return Results.Unauthorized();
            try { return Results.Ok(await service.SetHumanStatusAsync(organizationId, actor, request, cancellationToken)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException exception)
            { return Results.Conflict(new { error = "revision_conflict", message = exception.Message }); }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            { return Results.BadRequest(new { error = "invalid_personal_task", message = exception.Message }); }
        });

        orchestrationGroup.MapGet("/policy", async (
            Guid organizationId,
            Guid boardId,
            HttpContext http,
            IWorkOrchestrationService service,
            CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                var policy = await service.GetPolicyAsync(
                    organizationId, boardId, userId.Value, cancellationToken);
                return policy is null ? Results.NotFound() : Results.Ok(policy);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });

        orchestrationGroup.MapPost("/policy/revisions", async (
            Guid organizationId,
            Guid boardId,
            SaveWorkOrchestrationPolicyRequest request,
            HttpContext http,
            IWorkOrchestrationService service,
            CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                return Results.Ok(await service.SavePolicyRevisionAsync(
                    organizationId, boardId, userId.Value, request, cancellationToken));
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException exception)
            {
                return Results.Conflict(new { error = "revision_conflict", message = exception.Message });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = "invalid_orchestration_policy", message = exception.Message });
            }
        });

        orchestrationGroup.MapPost("/policy/publish", async (
            Guid organizationId,
            Guid boardId,
            PublishWorkOrchestrationPolicyRequest request,
            HttpContext http,
            IWorkOrchestrationService service,
            CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                return Results.Ok(await service.PublishPolicyRevisionAsync(
                    organizationId, boardId, userId.Value, request, cancellationToken));
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException exception)
            {
                return Results.Conflict(new { error = "revision_conflict", message = exception.Message });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = "invalid_orchestration_policy", message = exception.Message });
            }
        });

        orchestrationGroup.MapPost("/policy/software-template", async (
            Guid organizationId, Guid boardId,
            CreateSoftwareOrchestrationTemplateRequest request, HttpContext http,
            IWorkOrchestrationService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            if (request.MaximumQualityCycles is < 1 or > 10)
                return Results.BadRequest(new { error = "invalid_quality_cycles", message = "Maximum QA cycles must be between 1 and 10." });
            try
            {
                var retry = new CSweet.WorkManagement.Contracts.WorkOrchestrationRetryPolicy();
                var stages = new List<CSweet.WorkManagement.Contracts.WorkOrchestrationStageDefinition>
                {
                    new("ready", "Ready", "Queue", request.ReadyColumnId, "Wait until dependencies are complete.", "{}", "{}", 30, null, retry),
                    new("development", "Development", "AgentExecution", request.DevelopmentColumnId,
                        "Implement the approved ticket, validate it, and publish a reviewable pull request.", "{}",
                        "{\"type\":\"object\",\"required\":[\"repositoryConnectionId\",\"sourceBranch\",\"commitSha\",\"pullRequestUrl\",\"summary\"]}", 3600, null, retry),
                    new("dev-complete", "Dev Complete", "Queue", request.DevCompleteColumnId,
                        "Development is complete and ready for independent testing.", "{}", "{}", 30, null, retry),
                    new("quality", "Quality", "AgentExecution", request.QualityColumnId,
                        "Validate the exact development commit without modifying tracked source.", "{}",
                        "{\"type\":\"object\",\"required\":[\"verdict\",\"summary\",\"criteria\",\"validations\",\"findings\",\"remainingRisks\"]}", 1800, null, retry),
                    new("merge-decision", "Merge decision",
                        request.MergeMode == "Automatic" ? "Queue" : "ManagerApproval", request.ReadyToMergeColumnId,
                        "Authorize merge of the exact QA-approved commit.", "{}", "{}", 86400, 1, retry),
                    new("governed-merge", "Governed merge", "TrustedPlatformAction", request.ReadyToMergeColumnId,
                        "Revalidate and merge the exact QA-approved commit.", "{}", "{}", 300, 1, retry,
                        GovernedMergeWorkActionExecutor.ActionName),
                    new("done", "Done", "Terminal", request.DoneColumnId, "Work is complete.", "{}", "{}", 30, null, retry, null, true),
                    new("cancelled", "Cancelled", "Terminal", request.DoneColumnId, "Work was rejected.", "{}", "{}", 30, null, retry)
                };
                var transitions = new List<CSweet.WorkManagement.Contracts.WorkOrchestrationTransitionDefinition>
                {
                    new("ready", "ready", "development"),
                    new("development", "completed", "dev-complete"),
                    new("dev-complete", "ready", "quality"),
                    new("quality", "passed", "merge-decision"),
                    new("quality", "changes_requested", "development", request.MaximumQualityCycles),
                    new("merge-decision", request.MergeMode == "Automatic" ? "ready" : "approved", "governed-merge"),
                    new("merge-decision", "rejected", "cancelled"),
                    new("governed-merge", "merged", "done")
                };
                var revision = await service.SavePolicyRevisionAsync(
                    organizationId, boardId, userId.Value,
                    new("Software delivery", "ready", request.MergeMode,
                        new(100, 25, 10, 5, 1), stages, transitions, request.IdempotencyKey),
                    cancellationToken);
                return Results.Ok(await service.PublishPolicyRevisionAsync(
                    organizationId, boardId, userId.Value,
                    new(revision.RevisionId, $"{request.IdempotencyKey}:publish"), cancellationToken));
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (WorkOrchestrationValidationException exception) { return Results.BadRequest(new { error = "invalid_orchestration_policy", errors = exception.Errors }); }
        });

        orchestrationGroup.MapGet("/sprints/{sprintId:guid}/preflight", async (
            Guid organizationId, Guid boardId, Guid sprintId, HttpContext http,
            IWorkOrchestrationService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try { return Results.Ok(await service.PreflightAsync(organizationId, boardId, sprintId, userId.Value, cancellationToken)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });

        orchestrationGroup.MapGet("/sprints/{sprintId:guid}/execution", async (
            Guid organizationId, Guid boardId, Guid sprintId, HttpContext http,
            IWorkOrchestrationService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                var result = await service.GetExecutionAsync(organizationId, boardId, sprintId, userId.Value, cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });

        orchestrationGroup.MapPost("/sprints/{sprintId:guid}/start", async (
            Guid organizationId, Guid boardId, Guid sprintId,
            WorkOrchestrationControlRequest request, HttpContext http,
            IWorkOrchestrationService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try { return Results.Ok(await service.StartAsync(organizationId, boardId, sprintId, userId.Value, request, cancellationToken)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (WorkOrchestrationValidationException exception) { return Results.BadRequest(new { error = "sprint_preflight_failed", errors = exception.Errors }); }
            catch (DbUpdateConcurrencyException exception) { return Results.Conflict(new { error = "revision_conflict", message = exception.Message }); }
        });

        orchestrationGroup.MapPost("/sprints/{sprintId:guid}/{action}", async (
            Guid organizationId, Guid boardId, Guid sprintId, string action,
            WorkOrchestrationControlRequest request, HttpContext http,
            IWorkOrchestrationService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            if (action is not ("pause" or "resume" or "cancel")) return Results.NotFound();
            try
            {
                var result = await service.ControlAsync(organizationId, boardId, sprintId, userId.Value, action, request, cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = "orchestration_conflict", message = exception.Message }); }
            catch (DbUpdateConcurrencyException exception) { return Results.Conflict(new { error = "revision_conflict", message = exception.Message }); }
        });

        orchestrationGroup.MapPost("/stages/{stageExecutionId:guid}/retry", async (
            Guid organizationId, Guid boardId, Guid stageExecutionId,
            WorkOrchestrationControlRequest request, HttpContext http,
            IWorkOrchestrationService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try { return Results.Ok(await service.RetryAsync(organizationId, boardId, stageExecutionId, userId.Value, request, cancellationToken)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = "orchestration_conflict", message = exception.Message }); }
        });

        orchestrationGroup.MapPost("/stages/{stageExecutionId:guid}/manual-completion", async (
            Guid organizationId, Guid boardId, Guid stageExecutionId,
            CSweet.WorkManagement.Contracts.CompleteManualWorkStageRequest request, HttpContext http,
            IWorkOrchestrationService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try { return Results.Ok(await service.CompleteManualAsync(organizationId, boardId, stageExecutionId, userId.Value, request, cancellationToken)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = "orchestration_conflict", message = exception.Message }); }
        });

        orchestrationGroup.MapPost("/stages/{stageExecutionId:guid}/decision", async (
            Guid organizationId, Guid boardId, Guid stageExecutionId,
            CSweet.WorkManagement.Contracts.DecideWorkApprovalStageRequest request, HttpContext http,
            IWorkOrchestrationService service, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try { return Results.Ok(await service.DecideApprovalAsync(organizationId, boardId, stageExecutionId, userId.Value, request, cancellationToken)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = "orchestration_conflict", message = exception.Message }); }
        });

        repositoryGroup.MapGet("", async (
            Guid organizationId,
            HttpContext http,
            ISoftwareDevelopmentWorkService service,
            CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                return Results.Ok(await service.ListRepositoriesAsync(
                    organizationId, userId.Value, cancellationToken));
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });

        group.MapPut("/{boardId:guid}/items/{itemId:guid}/developer-assignment", async (
            Guid organizationId,
            Guid boardId,
            Guid itemId,
            AssignSoftwareDevelopmentWorkItemRequest request,
            HttpContext http,
            ISoftwareDevelopmentWorkService service,
            CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                return Results.Ok(await service.AssignAsync(
                    organizationId, boardId, itemId, userId.Value, request, cancellationToken));
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException exception)
            {
                return Results.Conflict(new { error = "revision_conflict", message = exception.Message });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = "invalid_developer_assignment", message = exception.Message });
            }
        });

        group.MapPost("/{boardId:guid}/items/{itemId:guid}/developer-assignment/unassign", async (
            Guid organizationId,
            Guid boardId,
            Guid itemId,
            UnassignSoftwareDevelopmentWorkItemRequest request,
            HttpContext http,
            ISoftwareDevelopmentWorkService service,
            CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                return Results.Ok(await service.UnassignAsync(
                    organizationId, boardId, itemId, userId.Value, request, cancellationToken));
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException exception)
            {
                return Results.Conflict(new { error = "revision_conflict", message = exception.Message });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = "invalid_developer_unassignment", message = exception.Message });
            }
        });

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

    private static async Task<PersonalTodoActor?> ResolvePersonalTodoActorAsync(
        Guid organizationId, HttpContext http, CSweetDbContext db,
        CancellationToken cancellationToken)
    {
        var applicationUserId = http.User.GetApplicationUserId();
        if (!applicationUserId.HasValue) return null;
        var organizationUserId = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId &&
                x.ApplicationUserId == applicationUserId && x.IsActive)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(cancellationToken);
        return organizationUserId.HasValue
            ? new PersonalTodoActor(organizationUserId.Value, null)
            : null;
    }
}
