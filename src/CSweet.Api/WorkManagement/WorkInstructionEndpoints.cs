using CSweet.Api.Auth;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.WorkManagement;

public static class WorkInstructionEndpoints
{
    public static void MapWorkInstructionEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{boardId:guid}/items/{itemId:guid}/instructions/preview", async (
            Guid organizationId, Guid boardId, Guid itemId, SelectWorkInstructionRequest request,
            HttpContext http, IWorkInstructionPublicationService service, CancellationToken token) =>
            await RunAsync(http, user => service.PreviewAsync(organizationId, boardId, itemId, user, request, token)));
        group.MapPost("/{boardId:guid}/items/{itemId:guid}/instructions/publish", async (
            Guid organizationId, Guid boardId, Guid itemId, PublishWorkInstructionRequest request,
            HttpContext http, IWorkInstructionPublicationService service, CancellationToken token) =>
            await RunAsync(http, user => service.PublishAsync(organizationId, boardId, itemId, user, request, token)));
        group.MapGet("/{boardId:guid}/items/{itemId:guid}/instructions/publications", async (
            Guid organizationId, Guid boardId, Guid itemId, Guid? cursor,
            HttpContext http, IWorkInstructionPublicationService service, CancellationToken token) =>
            await RunAsync(http, user => service.ListAsync(organizationId, boardId, itemId, user, cursor, token)));
    }

    private static async Task<IResult> RunAsync<T>(HttpContext http, Func<Guid, Task<T>> action)
    {
        var user = http.User.GetApplicationUserId();
        if (user is null) return Results.Unauthorized();
        try { return Results.Ok(await action(user.Value)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException error) { return Results.BadRequest(new { error = "invalid_instruction", message = error.Message }); }
        catch (DbUpdateConcurrencyException error) { return Results.Conflict(new { error = "instruction_changed", message = error.Message }); }
    }
}
