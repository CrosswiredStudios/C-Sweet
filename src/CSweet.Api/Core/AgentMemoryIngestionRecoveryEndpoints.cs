using System.Security.Claims;
using CSweet.Api.Auth;
using CSweet.Application.Core;
using CSweet.Contracts.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Core;

public static class AgentMemoryIngestionRecoveryEndpoints
{
    public static void MapMemoryIngestionRecoveryRoutes(this RouteGroupBuilder group)
    {
        group.MapGet("/ingestion/recovery", (Guid organizationId, Guid employeeId, string? cursor, int? limit,
            ClaimsPrincipal principal, [Microsoft.AspNetCore.Mvc.FromServices] IAgentMemoryIngestionRecoveryService recovery, CancellationToken token) =>
            Execute(principal, user => recovery.ListAsync(organizationId, employeeId, user, cursor, limit ?? 20, token)));
        group.MapGet("/episodes/{episodeId:guid}/ingestion-review", (Guid organizationId, Guid employeeId, Guid episodeId,
            ClaimsPrincipal principal, [Microsoft.AspNetCore.Mvc.FromServices] IAgentMemoryIngestionRecoveryService recovery, CancellationToken token) =>
            Execute(principal, user => recovery.PreviewAsync(organizationId, employeeId, episodeId, user, token)));
        group.MapPost("/episodes/{episodeId:guid}/ingestion-review", (Guid organizationId, Guid employeeId, Guid episodeId,
            RecoverMemoryIngestionRequest request, ClaimsPrincipal principal, [Microsoft.AspNetCore.Mvc.FromServices] IAgentMemoryIngestionRecoveryService recovery, CancellationToken token) =>
            Execute(principal, user => recovery.RecoverAsync(organizationId, employeeId, episodeId, user, request, token)));
    }
    private static async Task<IResult> Execute<T>(ClaimsPrincipal principal, Func<Guid, Task<T>> operation)
    {
        var user = principal.Identity?.IsAuthenticated == true ? principal.GetApplicationUserId() : null;
        if (!user.HasValue) return Results.Unauthorized();
        try { return Results.Ok(await operation(user.Value)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "memory_ingestion_review_changed" }); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_memory_ingestion_review" }); }
        catch (NotSupportedException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        catch (InvalidOperationException) { return Results.Conflict(new { error = "memory_ingestion_source_unavailable" }); }
        catch (System.Text.Json.JsonException) { return Results.Conflict(new { error = "memory_ingestion_source_unavailable" }); }
    }
}
