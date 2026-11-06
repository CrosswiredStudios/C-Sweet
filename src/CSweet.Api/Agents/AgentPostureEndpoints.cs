using System.Security.Claims;
using CSweet.Application.Agents;
using CSweet.Application.Setup;
using CSweet.Infrastructure.Agents;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using CSweet.Contracts.Agents;
using Microsoft.AspNetCore.Identity;

namespace CSweet.Api.Agents;

/// <summary>
/// Read and set the posture of one agent installation.
/// <para>
/// The posture is a human control surface, so it is restricted to the interactive application
/// scheme: an agent's own token can never raise its own authority.
/// </para>
/// </summary>
public static class AgentPostureEndpoints
{
    public static IEndpointRouteBuilder MapAgentPostureEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/agents/{installationId:guid}/posture")
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(IdentityConstants.ApplicationScheme)
                .RequireAuthenticatedUser());
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (HumanSession(http) is null) return Results.Forbid();
            http.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });

        group.MapGet("", async (
            Guid installationId,
            IAgentInstallationService installations,
            AgentPostureService postures,
            HttpContext http, CSweetDbContext db,
            CancellationToken cancellationToken) =>
        {
            // Authorize before lookup: an unmanaged installation is indistinguishable from a missing one.
            if (HumanSession(http) is not { } user || !await AgentGovernanceAccess.CanManageAsync(db, user, installationId,
                http.User.IsInRole(CSweet.Infrastructure.Auth.AuthenticationService.AdministratorRole), cancellationToken)) return Results.NotFound();
            var installation = await installations.GetAsync(installationId, cancellationToken);
            if (installation is null) return Results.NotFound();
            var assignment = await postures.GetAsync(installationId, cancellationToken);
            return Results.Ok(Respond(assignment, installation));
        });

        group.MapPut("", async (
            Guid installationId,
            SetAgentPostureRequest? request,
            HttpContext http,
            IAgentInstallationService installations,
            AgentPostureService postures,
            CSweetDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (HumanSession(http) is not { } changedBy) return Results.Forbid();
            if (request is null || !AgentAuthority.TryParse(request.Posture, out var posture))
                return Results.BadRequest(new
                {
                    error = "invalid_posture",
                    message = "Set the posture to Plan, Manual or Auto."
                });

            if (!await AgentGovernanceAccess.CanManageAsync(db, changedBy, installationId,
                http.User.IsInRole(CSweet.Infrastructure.Auth.AuthenticationService.AdministratorRole), cancellationToken)) return Results.NotFound();
            var installation = await installations.GetAsync(installationId, cancellationToken);
            if (installation is null) return Results.NotFound();

            AgentPostureAssignment assignment;
            try { assignment = await postures.SetAsync(installationId, posture, changedBy,
                http.User.IsInRole(CSweet.Infrastructure.Auth.AuthenticationService.AdministratorRole), cancellationToken); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "posture_changed", message = "Refresh the agent posture and try again." }); }
            return Results.Ok(Respond(assignment, installation));
        });

        return endpoints;
    }

    private static AgentPostureResponse Respond(AgentPostureAssignment assignment, AgentInstallationResponse installation) =>
        new(assignment.InstallationId,
            assignment.Posture.ToString(),
            assignment.Rule,
            AgentPostureRules.Title(assignment.Posture),
            assignment.IsDefault,
            assignment.ChangedAt,
            assignment.ChangedByApplicationUserId,
            installation.AgentId,
            installation.BusinessId);

    /// <summary>
    /// Exactly one interactive identity and nothing else. A request carrying both a cookie and a
    /// machine credential is refused rather than resolved, so posture changes cannot be made
    /// under an ambiguous identity.
    /// </summary>
    internal static Guid? HumanSession(HttpContext http)
    {
        var identities = http.User.Identities
            .Where(x => x.IsAuthenticated && x.AuthenticationType == IdentityConstants.ApplicationScheme)
            .Take(2)
            .ToArray();
        if (identities.Length != 1) return null;
        var identifiers = identities[0].FindAll(ClaimTypes.NameIdentifier).Take(2).ToArray();
        return identifiers.Length == 1 && Guid.TryParse(identifiers[0].Value, out var id) && id != Guid.Empty ? id : null;
    }
}
