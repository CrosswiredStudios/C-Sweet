using CSweet.Api.Auth;
using CSweet.Application.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.WorkManagement;

public static class WorkTrackingEndpoints
{
    public static IEndpointRouteBuilder MapWorkTrackingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/work/tracking").RequireAuthorization();
        group.MapGet("/{itemId:guid}", (Guid organizationId, Guid itemId, HttpContext http,
            CSweetDbContext db, ITrackedDelegationService service, CancellationToken token) =>
            Run(organizationId, http, db, actor => service.ReadTrackingAsync(organizationId, actor, itemId, token), token));
        group.MapPost("/{itemId:guid}/follow", (Guid organizationId, Guid itemId, FollowWorkRequest request, HttpContext http,
            CSweetDbContext db, ITrackedDelegationService service, CancellationToken token) =>
            request.WorkItemId != itemId ? Task.FromResult<IResult>(Results.BadRequest()) :
            Run(organizationId, http, db, actor => service.FollowAsync(organizationId, actor, request, true, token), token));
        group.MapPost("/{itemId:guid}/unfollow", (Guid organizationId, Guid itemId, FollowWorkRequest request, HttpContext http,
            CSweetDbContext db, ITrackedDelegationService service, CancellationToken token) =>
            request.WorkItemId != itemId ? Task.FromResult<IResult>(Results.BadRequest()) :
            Run(organizationId, http, db, actor => service.FollowAsync(organizationId, actor, request, false, token), token));
        return endpoints;
    }
    private static async Task<IResult> Run<T>(Guid org, HttpContext http, CSweetDbContext db,
        Func<Guid, Task<T>> action, CancellationToken token)
    {
        var user = http.User.GetApplicationUserId();
        var actor = await db.CoreOrganizationUsers.Where(x => x.OrganizationId == org && x.ApplicationUserId == user &&
            x.IsActive && x.ArchivedAt == null).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token);
        if (actor is null) return Results.Unauthorized();
        try { return Results.Ok(await action(actor.Value)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return Results.BadRequest(new { message = error.Message }); }
    }
}
