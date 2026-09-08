using System.Security.Claims;
using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Agents;

public static class ConnectorStandingPolicyEndpoints
{
    public static IEndpointRouteBuilder MapConnectorStandingPolicyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/core/organizations/{organizationId:guid}/connector-actions/{actionId:guid}/standing-policy")
            .RequireAuthorization(policy => policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme).RequireAuthenticatedUser());
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var userId = HumanSession(http);
            if (userId is null || !Guid.TryParse(http.Request.RouteValues["organizationId"]?.ToString(), out var organization))
                return Results.Forbid();
            var db = http.RequestServices.GetRequiredService<CSweetDbContext>();
            if (!await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.OrganizationId == organization &&
                    x.ApplicationUserId == userId && x.IsActive && x.EmployeeType == EmployeeType.Human &&
                    x.PermissionLevel == OrganizationPermissionLevel.Owner, http.RequestAborted)) return Results.Forbid();
            http.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException) { return Results.BadRequest(new { message = "Check every field choice, schedule, lifetime and limit before approving this policy." }); }
            catch (Exception error) when (error is InvalidOperationException or DbUpdateException or JsonException)
            { return Results.Conflict(new { message = "The reviewed account, action or policy changed. Reload and review it before trying again." }); }
        });
        group.MapGet("", async (Guid organizationId, Guid actionId, HttpContext http, CSweetDbContext db,
            ConnectorStandingPolicyService policies, CancellationToken ct) =>
        {
            var plan = await Plan(db, organizationId, actionId, ct);
            if (plan is null) return Results.NotFound();
            var user = HumanSession(http)!.Value;
            var current = await policies.GetAsync(organizationId, plan.RequesterInstallationId, user, plan.Capability, ct);
            try
            {
                var review = await policies.ReviewAsync(organizationId, plan.RequesterInstallationId, user, plan.Id, plan.PlanHash, ct);
                return Results.Ok(new ConnectorStandingPolicySetup(review, current));
            }
            catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException)
            {
                // An owner can still revoke a policy when account access or package authority is lost.
                return Results.Ok(new ConnectorStandingPolicySetup(null, current,
                    "This account or operation is no longer available for policy changes. Reconnect and review the current access first. Existing policy revocation remains available."));
            }
        });
        group.MapPut("", async (Guid organizationId, Guid actionId, ApproveConnectorStandingPolicyRequest request,
            HttpContext http, CSweetDbContext db, ConnectorStandingPolicyService policies, CancellationToken ct) =>
        {
            var plan = await Plan(db, organizationId, actionId, ct);
            if (plan is null) return Results.NotFound();
            if (request.TemplatePlanId != plan.Id || request.PlanHash != plan.PlanHash) return Results.Conflict();
            return Results.Ok(await policies.ApproveAsync(organizationId, plan.RequesterInstallationId,
                HumanSession(http)!.Value, request, ct));
        });
        group.MapPost("/revoke", async (Guid organizationId, Guid actionId, RevokeConnectorStandingPolicyRequest request,
            HttpContext http, CSweetDbContext db, ConnectorStandingPolicyService policies, CancellationToken ct) =>
        {
            var plan = await Plan(db, organizationId, actionId, ct);
            if (plan is null) return Results.NotFound();
            await policies.RevokeAsync(organizationId, plan.RequesterInstallationId, HumanSession(http)!.Value,
                plan.Capability, request.PolicyId, request.Revision, ct);
            return Results.NoContent();
        });
        return endpoints;
    }

    private static Guid? HumanSession(HttpContext http)
    {
        var identities = http.User.Identities.Where(x => x.IsAuthenticated && x.AuthenticationType == IdentityConstants.ApplicationScheme).Take(2).ToArray();
        if (identities.Length != 1) return null;
        var identifiers = identities[0].FindAll(ClaimTypes.NameIdentifier).Take(2).ToArray();
        return identifiers.Length == 1 && Guid.TryParse(identifiers[0].Value, out var id) && id != Guid.Empty ? id : null;
    }
    private static Task<ConnectorExecution?> Plan(CSweetDbContext db, Guid organizationId, Guid actionId, CancellationToken ct) =>
        db.ConnectorExecutions.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.ApprovalId == actionId &&
            db.ActionProposals.Any(p => p.Id == actionId && p.OrganizationId == organizationId && p.AgentInstallationId == x.RequesterInstallationId &&
                p.ActionType == ConnectorActionApprovalService.ActionType), ct);
}
