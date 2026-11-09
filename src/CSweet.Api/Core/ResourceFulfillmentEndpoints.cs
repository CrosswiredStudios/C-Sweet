using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Api.Auth;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Core;

public static class ResourceFulfillmentEndpoints
{
    public static IEndpointRouteBuilder MapResourceFulfillmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/core/organizations/{organizationId:guid}/resources", async (Guid organizationId,
            Guid? workstreamId, HttpContext http, CSweetDbContext db, CancellationToken token) =>
        {
            var user = http.User.GetApplicationUserId();
            if (user is null) return Results.Forbid();
            var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
                x.ApplicationUserId == user && x.IsActive && x.ArchivedAt == null, token);
            if (actor is null) return Results.Forbid();
            var query = db.PluginOperationalStates.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
                x.Kind == ResourceFulfillmentService.StateKind);
            if (workstreamId is { } project)
            {
                var marker = "\"workstreamId\":\"" + project.ToString("D") + "\"";
                query = query.Where(x => x.PayloadJson.Contains(marker));
            }
            if (actor.PermissionLevel != OrganizationPermissionLevel.Owner)
            {
                var approver = "\"approverId\":\"" + actor.Id.ToString("D") + "\"";
                var requester = "\"requesterId\":\"" + actor.Id.ToString("D") + "\"";
                query = query.Where(x => x.PayloadJson.Contains(approver) || x.PayloadJson.Contains(requester));
            }
            var rows = await query.OrderByDescending(x => x.UpdatedAt).Take(100).ToListAsync(token);
            var states = rows.Select(x => JsonSerializer.Deserialize<ResourceRequestState>(x.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .Where(x => (workstreamId is null || x.WorkstreamId == workstreamId) &&
                    (actor.PermissionLevel == OrganizationPermissionLevel.Owner || x.ApproverId == actor.Id || x.RequesterId == actor.Id)).ToArray();
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var response = states.Select(state => {
                var value = JsonSerializer.SerializeToNode(state, json)!.AsObject();
                value["canDecide"] = state.ApproverId == actor.Id && state.Status == "PendingApproval";
                value["canRetry"] = state.ApproverId == actor.Id && state.Status is "Failed" or "NeedsAttention" or "Revoked";
                return value;
            }).ToArray();
            return Results.Ok(response);
        }).RequireAuthorization();
        endpoints.MapPost("/api/core/organizations/{organizationId:guid}/resources/{requestId:guid}/retry", async (
            Guid organizationId, Guid requestId, ResourceDecision decision, HttpContext http, CSweetDbContext db,
            ResourceFulfillmentService resources, CancellationToken token) =>
        {
            var user = http.User.GetApplicationUserId();
            var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
                x.ApplicationUserId == user && x.IsActive && x.ArchivedAt == null, token);
            if (actor is null) return Results.Forbid();
            if (decision.RequestId != requestId || decision.Decision != "Retry") return Results.BadRequest();
            try { return Results.Ok(await resources.DecideAsync(actor, decision, token)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException)
            { return Results.Conflict(new { error = "resource_retry_stale", message = error.Message }); }
        }).RequireAuthorization();
        return endpoints;
    }
}
