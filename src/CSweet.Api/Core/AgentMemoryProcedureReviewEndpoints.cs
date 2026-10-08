using System.Security.Claims;
using CSweet.Api.Auth;
using CSweet.Application.Core;
using CSweet.Contracts.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Core;

public static class AgentMemoryProcedureReviewEndpoints
{
    public static void MapMemoryProcedureReviewRoutes(this RouteGroupBuilder group)
    {
        group.MapGet("/procedures/{procedureId:guid}/review", (Guid organizationId, Guid employeeId, Guid procedureId,
            ClaimsPrincipal principal, IAgentMemoryReviewService review, CancellationToken token) =>
            Execute(principal, user => review.GetProcedureAsync(organizationId, employeeId, procedureId, user, token)));
        group.MapPost("/procedures/{procedureId:guid}/review", (Guid organizationId, Guid employeeId, Guid procedureId,
            ReviewMemoryProcedureRequest request, ClaimsPrincipal principal, IAgentMemoryReviewService review, CancellationToken token) =>
            Execute(principal, user => review.ReviewProcedureAsync(organizationId, employeeId, procedureId, user, request, token)));
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
