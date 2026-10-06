using CSweet.Api.Auth;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace CSweet.Api.Core;

public static class CompanyDashboardEndpoints
{
    public static IEndpointRouteBuilder MapCompanyDashboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/core/organizations/{organizationId:guid}/dashboard").RequireAuthorization();
        group.MapGet("/hiring", async (Guid organizationId, HttpContext http, CSweetDbContext db,
            CSweet.Application.Core.IHiringService hiring, CancellationToken token) =>
        {
            var actor = await ActorAsync(organizationId, http, db, token);
            return actor is null || actor.PermissionLevel < OrganizationPermissionLevel.Manager
                ? Results.Forbid()
                : Results.Ok(new HiringBacklogResponse(await hiring.ListRecommendationsAsync(organizationId, token)));
        });
        group.MapGet("/activity", async (Guid organizationId, HttpContext http, CurrentActivityService activity,
            int? offset, bool? projectsOnly, CancellationToken token) =>
        {
            var user = http.User.GetApplicationUserId();
            if (user is null) return Results.Unauthorized();
            if (offset is < 0) return Results.BadRequest();
            try { return Results.Ok(await activity.ReadAsync(organizationId, user.Value, offset ?? 0, projectsOnly ?? false, token)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });
        group.MapGet("/activity/{employeeId:guid}/{attemptId:guid}/feed", async (Guid organizationId, Guid employeeId,
            Guid attemptId, Guid? workItemId, long? afterSequence, HttpContext http, CurrentActivityService activity,
            CSweet.Application.Setup.IAuditEventWriter audit, CancellationToken token) =>
        {
            var user = http.User.GetApplicationUserId();
            if (user is null) return Results.Unauthorized();
            if (afterSequence is < 0) return Results.BadRequest();
            try
            {
                var result = await activity.FeedAsync(organizationId, user.Value, employeeId, attemptId, workItemId, afterSequence ?? 0, token);
                await audit.AppendAsync(new CSweet.Application.Setup.AuditEventWriteRequest("security.activity-diagnostics.read", "SecurityAccess",
                    OrganizationId: organizationId, EntityType: "AgentWorkAttempt", EntityId: attemptId,
                    Actor: new CSweet.Application.Setup.AuditActor("Human", ApplicationUserId: user),
                    Summary: "Current activity diagnostic evidence inspected."), token);
                return Results.Ok(result);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        group.MapGet("/activity/{employeeId:guid}/models/{runId:guid}/feed", async (Guid organizationId, Guid employeeId,
            Guid runId, long? afterSequence, HttpContext http, CurrentActivityService activity,
            CSweet.Application.Setup.IAuditEventWriter audit, CancellationToken token) =>
        {
            var user = http.User.GetApplicationUserId();
            if (user is null) return Results.Unauthorized();
            if (afterSequence is < 0) return Results.BadRequest();
            try
            {
                var result = await activity.ModelFeedAsync(organizationId, user.Value, employeeId, runId, afterSequence ?? 0, token);
                await audit.AppendAsync(new CSweet.Application.Setup.AuditEventWriteRequest("security.activity-diagnostics.read", "SecurityAccess",
                    OrganizationId: organizationId, EntityType: "AgentRunLog", EntityId: runId,
                    Actor: new CSweet.Application.Setup.AuditActor("Human", ApplicationUserId: user),
                    Summary: "Current activity model evidence inspected."), token);
                return Results.Ok(result);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        group.MapGet("", async (Guid organizationId, HttpContext http, CSweetDbContext db, CompanyDashboardService service, CancellationToken token) =>
        {
            var actor = await ActorAsync(organizationId, http, db, token);
            return actor is null || actor.PermissionLevel < OrganizationPermissionLevel.Manager
                ? Results.Forbid() : Results.Ok(await service.ReadAsync(organizationId, token));
        });
        group.MapGet("/layout", async (Guid organizationId, HttpContext http, CSweetDbContext db, CompanyDashboardService service, CancellationToken token) =>
        {
            var actor = await ActorAsync(organizationId, http, db, token);
            return actor is null ? Results.Forbid() : Results.Ok(new DashboardLayoutRequest(await service.LayoutAsync(organizationId, actor.Id, token)));
        });
        group.MapPut("/layout", async (Guid organizationId, DashboardLayoutRequest request, HttpContext http, CSweetDbContext db, CompanyDashboardService service, CancellationToken token) =>
        {
            var actor = await ActorAsync(organizationId, http, db, token);
            if (actor is null) return Results.Forbid();
            if (!DashboardWidgets.IsValid(request.Order)) return Results.BadRequest(new { error = "Include each dashboard widget exactly once." });
            await service.SaveLayoutAsync(organizationId, actor.Id, request, token);
            return Results.NoContent();
        });
        return endpoints;
    }
    private static Task<OrganizationUser?> ActorAsync(Guid organizationId, HttpContext http, CSweetDbContext db, CancellationToken token)
    {
        var userId = http.User.GetApplicationUserId();
        return db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.ApplicationUserId == userId && userId != null && x.IsActive, token);
    }
}
