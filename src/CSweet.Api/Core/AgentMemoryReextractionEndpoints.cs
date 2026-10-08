using System.Security.Claims;
using CSweet.Api.Auth;
using CSweet.Application.Core;
using CSweet.Contracts.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Core;

public static class AgentMemoryReextractionEndpoints
{
    public static void MapMemoryReextractionRoutes(this RouteGroupBuilder group)
    {
        group.MapGet("/ingestion/reextraction", (Guid organizationId, Guid employeeId, string? cursor, int? limit,
            ClaimsPrincipal principal, [Microsoft.AspNetCore.Mvc.FromServices] IAgentMemoryReextractionService recovery, CancellationToken token) =>
            Execute(principal, user => recovery.ListAsync(organizationId, employeeId, user, cursor, limit ?? 20, token)));
        group.MapGet("/jobs/{jobId:guid}/reextraction-review", (Guid organizationId, Guid employeeId, Guid jobId,
            ClaimsPrincipal principal, [Microsoft.AspNetCore.Mvc.FromServices] IAgentMemoryReextractionService recovery, CancellationToken token) =>
            Execute(principal, user => recovery.PreviewAsync(organizationId, employeeId, jobId, user, token)));
        group.MapPost("/jobs/{jobId:guid}/reextraction-review", (Guid organizationId, Guid employeeId, Guid jobId,
            ReviewMemoryReextractionRequest request, ClaimsPrincipal principal,
            [Microsoft.AspNetCore.Mvc.FromServices] IAgentMemoryReextractionService recovery, CancellationToken token) =>
            Execute(principal, user => recovery.ReviewAsync(organizationId, employeeId, jobId, user, request, token)));
    }
    private static async Task<IResult> Execute<T>(ClaimsPrincipal principal, Func<Guid, Task<T>> operation)
    {
        var user = principal.Identity?.IsAuthenticated == true ? principal.GetApplicationUserId() : null;
        if (!user.HasValue) return Results.Unauthorized();
        try { return Results.Ok(await operation(user.Value)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "memory_reextraction_review_changed" }); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_memory_reextraction_review" }); }
        catch (NotSupportedException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        catch (Exception error) when (error is InvalidOperationException or System.Text.Json.JsonException)
        { return Results.Conflict(new { error = "memory_reextraction_unavailable" }); }
    }
}
