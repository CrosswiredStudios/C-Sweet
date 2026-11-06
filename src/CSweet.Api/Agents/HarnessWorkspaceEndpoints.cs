using CSweet.Contracts.Agents;
using CSweet.Infrastructure.Agents;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Agents;

public static class HarnessWorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapHarnessWorkspaceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/agents/{installationId:guid}/harness")
            .RequireAuthorization(policy => policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme).RequireAuthenticatedUser());
        group.AddEndpointFilter(async (context, next) => {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            if (AgentPostureEndpoints.HumanSession(http) is not { } user ||
                !Guid.TryParse(http.Request.RouteValues["installationId"]?.ToString(), out var installation) ||
                !await AgentGovernanceAccess.CanManageAsync(http.RequestServices.GetRequiredService<CSweetDbContext>(), user, installation,
                    http.User.IsInRole(CSweet.Infrastructure.Auth.AuthenticationService.AdministratorRole), http.RequestAborted)) return Results.Forbid();
            try { return await next(context); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "The policy changed. Refresh and try again." }); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        group.MapGet("", (Guid installationId, HarnessWorkspaceService service, CancellationToken token) => service.ReadAsync(installationId, token));
        group.MapPut("", (Guid installationId, SaveHarnessPolicy request, HttpContext http, HarnessWorkspaceService service, CancellationToken token) =>
            service.SaveAsync(installationId, AgentPostureEndpoints.HumanSession(http)!.Value, request, token));
        group.MapPost("/skills", (Guid installationId, PublishHarnessSkill request, HttpContext http, HarnessWorkspaceService service, CancellationToken token) =>
            service.PublishAsync(installationId, AgentPostureEndpoints.HumanSession(http)!.Value, request, token));
        group.MapDelete("/skills/{revisionId:guid}", async (Guid installationId, Guid revisionId, HttpContext http, HarnessWorkspaceService service, CancellationToken token) => {
            await service.DisableSkillAsync(installationId, revisionId, AgentPostureEndpoints.HumanSession(http)!.Value, token);
            return Results.NoContent();
        });
        return endpoints;
    }
}
