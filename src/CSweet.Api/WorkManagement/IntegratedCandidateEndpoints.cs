using CSweet.Api.Auth;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.WorkManagement;

internal static class IntegratedCandidateEndpoints
{
    internal static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/{boardId:guid}/items/{itemId:guid}/integrated-candidate", async (
            Guid organizationId, Guid boardId, Guid itemId, HttpContext http,
            IWorkOrchestrationService service, CancellationToken ct) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try { return Results.Ok(await service.GetIntegratedCandidateAsync(organizationId, boardId, itemId, userId.Value, ct)); }
            catch (UnauthorizedAccessException error) { return Results.Json(new { message = error.Message }, statusCode: 403); }
            catch (InvalidOperationException error) { return Results.Conflict(new { message = error.Message }); }
        });
        group.MapPost("/{boardId:guid}/items/{itemId:guid}/resubmit-integrated-candidate", async (
            Guid organizationId, Guid boardId, Guid itemId, ResubmitIntegratedCandidateRequest request, HttpContext http,
            IWorkOrchestrationService service, CancellationToken ct) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            try
            {
                await service.ResubmitIntegratedCandidateAsync(organizationId, boardId, itemId, userId.Value, request, ct);
                return Results.Ok();
            }
            catch (UnauthorizedAccessException error) { return Results.Json(new { message = error.Message }, statusCode: 403); }
            catch (DbUpdateConcurrencyException error) { return Results.Conflict(new { message = error.Message }); }
            catch (InvalidOperationException error) { return Results.Conflict(new { message = error.Message }); }
            catch (ArgumentException error) { return Results.BadRequest(new { message = error.Message }); }
        });
    }
}
