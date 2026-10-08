using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using CSweet.Api.Auth;
using CSweet.Application.Core;
using CSweet.Contracts.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Core;

public static class AgentMemorySuppressionEndpoints
{
    public static void MapMemorySuppressionRoutes(this RouteGroupBuilder group)
    {
        group.MapGet("/episodes/{episodeId:guid}/suppression", (Guid organizationId, Guid employeeId, Guid episodeId,
            ClaimsPrincipal principal, [FromServices] IAgentMemorySuppressionService review, CancellationToken token) =>
            Execute(principal, user => review.GetSuppressionAsync(organizationId, employeeId, episodeId, user, token)));
        group.MapPost("/episodes/{episodeId:guid}/suppression", (Guid organizationId, Guid employeeId, Guid episodeId,
            SuppressMemorySourceRequest request, ClaimsPrincipal principal, [FromServices] IAgentMemorySuppressionService review, CancellationToken token) =>
            Execute(principal, user => review.SuppressSourceAsync(organizationId, employeeId, episodeId, user, request, token)));
    }
    private static async Task<IResult> Execute<T>(ClaimsPrincipal principal, Func<Guid, Task<T>> operation)
    {
        var user = principal.Identity?.IsAuthenticated == true ? principal.GetApplicationUserId() : null;
        if (!user.HasValue) return Results.Unauthorized();
        try { return Results.Ok(await operation(user.Value)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "memory_review_changed" }); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_memory_review" }); }
        catch (NotSupportedException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        catch (InvalidOperationException) { return Results.Conflict(new { error = "memory_review_source_unavailable" }); }
    }
}
