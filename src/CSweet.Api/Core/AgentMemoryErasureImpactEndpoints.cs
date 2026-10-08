using System.Security.Claims;
using CSweet.Api.Auth;
using CSweet.Application.Core;
using CSweet.Contracts.Memory;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Core;

public static class AgentMemoryErasureImpactEndpoints
{
    public static void MapMemoryErasureImpactRoutes(this RouteGroupBuilder group)
    {
        group.MapGet("/episodes/{episodeId:guid}/erasure-impact", async (Guid organizationId, Guid employeeId, Guid episodeId,
            ClaimsPrincipal principal, HttpResponse response, [FromServices] IAgentMemoryErasureImpactService review, CancellationToken token) =>
        {
            response.Headers.CacheControl = "no-store";
            var user = principal.Identity?.IsAuthenticated == true ? principal.GetApplicationUserId() : null;
            if (!user.HasValue) return Results.Unauthorized();
            try { return Results.Ok(await review.GetErasureImpactAsync(organizationId, employeeId, episodeId, user.Value, token)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "memory_review_changed" }); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_memory_review" }); }
            catch (NotSupportedException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            catch (InvalidOperationException) { return Results.Conflict(new { error = "memory_erasure_impact_unavailable" }); }
        });
        group.MapPost("/episodes/{episodeId:guid}/erase", async (Guid organizationId, Guid employeeId, Guid episodeId,
            EraseMemorySourceRequest request, ClaimsPrincipal principal, HttpResponse response,
            [FromServices] IAgentMemoryErasureImpactService review, CancellationToken token) =>
        {
            response.Headers.CacheControl = "no-store";
            var user = principal.Identity?.IsAuthenticated == true ? principal.GetApplicationUserId() : null;
            if (!user.HasValue) return Results.Unauthorized();
            return await ExecuteAsync(() => review.EraseSourceAsync(organizationId, employeeId, episodeId, user.Value, request, token));
        });
        group.MapGet("/erasure-operations/{operationId:guid}", async (Guid organizationId, Guid employeeId, Guid operationId,
            ClaimsPrincipal principal, HttpResponse response, [FromServices] IAgentMemoryErasureImpactService review, CancellationToken token) =>
        {
            response.Headers.CacheControl = "no-store";
            var user = principal.Identity?.IsAuthenticated == true ? principal.GetApplicationUserId() : null;
            if (!user.HasValue) return Results.Unauthorized();
            return await ExecuteAsync(() => review.GetErasureStatusAsync(organizationId, employeeId, operationId, user.Value, token));
        });
    }

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "memory_review_changed" }); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_memory_review" }); }
        catch (NotSupportedException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        catch (InvalidOperationException) { return Results.Conflict(new { error = "memory_erasure_unavailable" }); }
    }
}
