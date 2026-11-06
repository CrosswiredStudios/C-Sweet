using CSweet.Application.Agents;
using CSweet.Contracts.Agents;
using Microsoft.AspNetCore.Identity;

namespace CSweet.Api.Agents;

public static class CapabilityApprovalEndpoints
{
    public static IEndpointRouteBuilder MapCapabilityApprovalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/core/organizations/{organizationId:guid}/capability-approvals")
            .RequireAuthorization(policy => policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme).RequireAuthenticatedUser());
        group.MapGet("", async (Guid organizationId, HttpContext http, IAgentCapabilityApprovalService service, CancellationToken token) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (AgentPostureEndpoints.HumanSession(http) is not { } user) return Results.Forbid();
            return Results.Ok(await service.ListAsync(organizationId, user, IsAdministrator(http), token));
        });
        group.MapPost("/{approvalId:guid}/decision", async (Guid organizationId, Guid approvalId,
            DecideCapabilityApprovalRequest request, HttpContext http, IAgentCapabilityApprovalService service, CancellationToken token) =>
        {
            if (AgentPostureEndpoints.HumanSession(http) is not { } user) return Results.Forbid();
            try { return Results.Ok(await service.DecideAsync(organizationId, approvalId, user, IsAdministrator(http), request.Approve, token)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException error) { return Results.Conflict(new { error = error.Message }); }
            catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException) { return Results.Conflict(); }
        });
        // Releases an execution that was claimed but never completed, without repeating it.
        group.MapPost("/{approvalId:guid}/abandon", async (Guid organizationId, Guid approvalId,
            HttpContext http, IAgentCapabilityApprovalService service, CancellationToken token) =>
        {
            if (AgentPostureEndpoints.HumanSession(http) is not { } user) return Results.Forbid();
            try { return Results.Ok(await service.AbandonAsync(organizationId, approvalId, user, IsAdministrator(http), token)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException error) { return Results.Conflict(new { error = error.Message }); }
        });
        return endpoints;
    }

    private static bool IsAdministrator(HttpContext http) =>
        http.User.IsInRole(CSweet.Infrastructure.Auth.AuthenticationService.AdministratorRole);
}
