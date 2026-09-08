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
