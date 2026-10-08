using System.Security.Claims;
using CSweet.Api.Auth;
using CSweet.Application.Core;
using CSweet.Contracts.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Core;

public static class AgentMemoryTransferEndpoints
{
    public static void MapMemoryTransferRoutes(this RouteGroupBuilder group)
    {
        group.MapPost("/transfers", (Guid organizationId, Guid employeeId, PrepareMemoryTransferRequest request,
            ClaimsPrincipal principal, IAgentMemoryTransferService transfers, CancellationToken token) =>
            Execute(principal, user => transfers.PrepareAsync(organizationId, employeeId, user, request, token)));
        group.MapGet("/transfers", (Guid organizationId, Guid employeeId, Guid? cursor, ClaimsPrincipal principal,
            IAgentMemoryTransferService transfers, CancellationToken token) =>
            Execute(principal, user => transfers.ListAsync(organizationId, employeeId, user, cursor, token)));
        group.MapGet("/transfers/{packageId:guid}", (Guid organizationId, Guid employeeId, Guid packageId, ClaimsPrincipal principal,
            IAgentMemoryTransferService transfers, CancellationToken token) =>
            Execute(principal, user => transfers.GetAsync(organizationId, employeeId, packageId, user, token)));
        group.MapPost("/transfers/{packageId:guid}", (Guid organizationId, Guid employeeId, Guid packageId,
            TransitionMemoryTransferRequest request, ClaimsPrincipal principal, IAgentMemoryTransferService transfers, CancellationToken token) =>
            Execute(principal, user => transfers.TransitionAsync(organizationId, employeeId, packageId, user, request, token)));
    }

    private static async Task<IResult> Execute<T>(ClaimsPrincipal principal, Func<Guid, Task<T>> operation)
    {
        var user = principal.Identity?.IsAuthenticated == true ? principal.GetApplicationUserId() : null;
        if (!user.HasValue) return Results.Unauthorized();
        try { return Results.Ok(await operation(user.Value)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "memory_transfer_changed" }); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_memory_transfer" }); }
        catch (NotSupportedException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        catch (InvalidOperationException) { return Results.Conflict(new { error = "memory_transfer_source_unavailable" }); }
    }
}
