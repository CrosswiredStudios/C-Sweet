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
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (UnauthorizedAccessException exception)
            {
                return Results.Json(new { error = "owner_required", message = exception.Message },
                    statusCode: StatusCodes.Status403Forbidden);
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
        return endpoints;
    }
}
