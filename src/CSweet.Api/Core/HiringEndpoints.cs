using CSweet.Api.Auth;
using CSweet.Application.Communications;
using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Core;

namespace CSweet.Api.Core;

public static class HiringEndpoints
{
    public static IEndpointRouteBuilder MapHiringEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/core/organizations/{organizationId:guid}/hiring");

        group.MapGet("", async (
            Guid organizationId,
            HttpContext http,
            IHiringService service,
            ICommunicationHubService communications,
            CancellationToken cancellationToken) =>
        {
            var applicationUserId = http.User.GetApplicationUserId();
            var actorId = applicationUserId.HasValue
                ? await communications.ResolveOrganizationUserIdAsync(
                    organizationId, applicationUserId.Value, cancellationToken)
                : null;
            return Results.Ok((await service.GetDashboardAsync(organizationId, cancellationToken)) with
            {
                CurrentOrganizationUserId = actorId
            });
        });

        group.MapPost("/resource-changes/{requestId:guid}/decide", async (
            Guid organizationId,
            Guid requestId,
            ResourceChangeDecisionRequest request,
            HttpContext http,
            IResourceChangeService service,
            CancellationToken cancellationToken) =>
        {
            var applicationUserId = http.User.GetApplicationUserId();
            if (!applicationUserId.HasValue) return Results.Forbid();
            if (request.RequestId != requestId)
                return Results.BadRequest(new { error = "request_mismatch", message = "The route and payload request IDs must match." });
            try
            {
                return Results.Ok(await service.DecideForUserAsync(
                    organizationId, applicationUserId.Value, request, cancellationToken));
            }
            catch (UnauthorizedAccessException exception)
            {
                return Results.Json(new { error = "manager_required", message = exception.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = "invalid_decision", message = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = "decision_conflict", message = exception.Message });
            }
        });

        group.MapGet("/staffing-replenishments", async (
            Guid organizationId,
            HttpContext http,
            IStaffingReplenishmentService service,
            CancellationToken cancellationToken) =>
        {
            if (!http.User.GetApplicationUserId().HasValue) return Results.Forbid();
            return Results.Ok(await service.ListForDashboardAsync(organizationId, cancellationToken));
        });

        group.MapPost("/staffing-replenishments/{requestId:guid}/decide", async (
            Guid organizationId,
            Guid requestId,
            StaffingReplenishmentDecisionRequest request,
            HttpContext http,
            IStaffingReplenishmentService service,
            CancellationToken cancellationToken) =>
        {
            var applicationUserId = http.User.GetApplicationUserId();
            if (!applicationUserId.HasValue) return Results.Forbid();
            if (request.RequestId != requestId)
                return Results.BadRequest(new { error = "request_mismatch", message = "The route and payload request IDs must match." });
            try
            {
                return Results.Ok(await service.DecideForUserAsync(
                    organizationId, applicationUserId.Value, request, cancellationToken));
            }
            catch (UnauthorizedAccessException exception)
            {
                return Results.Json(new { error = "manager_required", message = exception.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = "invalid_decision", message = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = "decision_conflict", message = exception.Message });
            }
        });

        group.MapPost("/marketplace/preview", async (
            Guid organizationId,
            PreviewMarketplaceHireRequest request,
            HttpContext http,
            IAgentHireOrchestrator service,
            CancellationToken cancellationToken) =>
        {
            var applicationUserId = http.User.GetApplicationUserId();
            if (!applicationUserId.HasValue) return Results.Forbid();
            try
            {
                return Results.Ok(await service.PreviewAsync(
                    organizationId,
                    applicationUserId.Value,
                    request,
                    cancellationToken));
            }
            catch (UnauthorizedAccessException exception)
            {
                return Results.Json(new { error = "owner_required", message = exception.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = "invalid_hire", message = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = "hire_unavailable", message = exception.Message });
            }
            catch (AgentImportPreviewException exception)
            {
                return Results.Conflict(new { error = "agent_source_unavailable", message = exception.Message });
            }
        });

        group.MapPost("/workflows/{workflowId:guid}/confirm", async (Guid organizationId, Guid workflowId,
            ConfirmHiringWorkflowRequest request, HttpContext http, IAgentHireOrchestrator service,
            CancellationToken cancellationToken) =>
        {
            var applicationUserId = http.User.GetApplicationUserId();
            if (!applicationUserId.HasValue) return Results.Forbid();
            try
            {
                var result = await service.ConfirmAsync(organizationId, workflowId,
                    applicationUserId.Value, request, cancellationToken);
                if (result is null) return Results.NotFound();
                return AgentHireOperationStatuses.IsActive(result.Status) ||
                       result.Status == AgentHireOperationStatuses.AwaitingConfirmation
                    ? Results.Accepted($"/api/core/hiring/operations/{result.Id:D}", result)
                    : Results.Ok(result);
            }
            catch (UnauthorizedAccessException exception)
            {
                return Results.Json(new { error = "owner_required", message = exception.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            catch (AgentDefinitionBuildPendingException exception)
            {
                return Results.Conflict(new
                {
                    error = "agent_build_pending",
                    message = exception.Message,
                    definitionId = exception.DefinitionId
                });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = "approval_invalidated", message = exception.Message });
            }
            catch (AgentInstallationException exception)
            {
                return Results.Conflict(new { error = "installation_rejected", message = exception.Message });
            }
        });
        group.MapPost("/workflows/{workflowId:guid}/decide", async (Guid organizationId, Guid workflowId,
            DecideHiringWorkflowRequest request, HttpContext http, IHiringService service,
            CancellationToken cancellationToken) =>
        {
            var applicationUserId = http.User.GetApplicationUserId();
            if (!applicationUserId.HasValue) return Results.Forbid();
            try
            {
                var result = await service.DecideWorkflowAsync(
                    organizationId, workflowId, applicationUserId.Value, request, cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (UnauthorizedAccessException exception)
            {
                return Results.Json(new { error = "owner_required", message = exception.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = "invalid_decision", message = exception.Message });
            }
            catch (AgentDefinitionBuildPendingException exception)
            {
                return Results.Conflict(new
                {
                    error = "agent_build_pending",
                    message = exception.Message,
                    definitionId = exception.DefinitionId
                });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = "approval_invalidated", message = exception.Message });
            }
            catch (AgentInstallationException exception)
            {
                return Results.Conflict(new { error = "installation_rejected", message = exception.Message });
            }
        });
        group.MapPost("/workflows/{workflowId:guid}/cancel-preview", async (
            Guid organizationId,
            Guid workflowId,
            HttpContext http,
            IHiringService service,
            CancellationToken cancellationToken) =>
        {
            var applicationUserId = http.User.GetApplicationUserId();
            if (!applicationUserId.HasValue) return Results.Forbid();
            try
            {
                var result = await service.CancelMarketplacePreviewAsync(
                    organizationId, workflowId, applicationUserId.Value, cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (UnauthorizedAccessException exception)
            {
                return Results.Json(new { error = "owner_required", message = exception.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = "hire_already_started", message = exception.Message });
            }
        });

        endpoints.MapGet("/api/core/hiring/operations", async (
            HttpContext http,
            IAgentHireOperationService service,
            CancellationToken cancellationToken) =>
        {
            var applicationUserId = http.User.GetApplicationUserId();
            return applicationUserId.HasValue
                ? Results.Ok(await service.ListForUserAsync(applicationUserId.Value, cancellationToken))
                : Results.Forbid();
        });

        endpoints.MapGet("/api/core/hiring/operations/{operationId:guid}", async (
            Guid operationId,
            HttpContext http,
            IAgentHireOperationService service,
            CancellationToken cancellationToken) =>
        {
            var applicationUserId = http.User.GetApplicationUserId();
            if (!applicationUserId.HasValue) return Results.Forbid();
            try
            {
                var operation = await service.GetForUserAsync(operationId, applicationUserId.Value, cancellationToken);
                return operation is null ? Results.NotFound() : Results.Ok(operation);
            }
            catch (UnauthorizedAccessException exception)
            {
                return Results.Json(new { error = "owner_required", message = exception.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            }
        });

        endpoints.MapPost("/api/core/hiring/operations/{operationId:guid}/retry", async (
            Guid operationId,
            HttpContext http,
            IAgentHireOperationService service,
            CancellationToken cancellationToken) =>
        {
            var applicationUserId = http.User.GetApplicationUserId();
            if (!applicationUserId.HasValue) return Results.Forbid();
            try
            {
                var operation = await service.RetryAsync(operationId, applicationUserId.Value, cancellationToken);
                return operation is null ? Results.NotFound() : Results.Accepted(
                    $"/api/core/hiring/operations/{operation.Id:D}", operation);
            }
            catch (UnauthorizedAccessException exception)
            {
                return Results.Json(new { error = "owner_required", message = exception.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = "operation_not_retryable", message = exception.Message });
            }
        });

        endpoints.MapPost("/api/core/hiring/operations/{operationId:guid}/dismiss", async (
            Guid operationId,
            HttpContext http,
            IAgentHireOperationService service,
            CancellationToken cancellationToken) =>
        {
            var applicationUserId = http.User.GetApplicationUserId();
            if (!applicationUserId.HasValue) return Results.Forbid();
            try
            {
                var operation = await service.DismissAsync(operationId, applicationUserId.Value, cancellationToken);
                return operation is null ? Results.NotFound() : Results.Ok(operation);
            }
            catch (UnauthorizedAccessException exception)
            {
                return Results.Json(new { error = "owner_required", message = exception.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = "operation_active", message = exception.Message });
            }
        });
        return endpoints;
    }
}
