using CSweet.Api.Auth;
using CSweet.Application.Core;
using CSweet.Contracts.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Core;

public static class TeamEndpoints
{
    public static IEndpointRouteBuilder MapTeamEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/core/organizations/{organizationId:guid}/teams")
            .RequireAuthorization();

        group.MapGet("", async (
            Guid organizationId,
            bool includeArchived,
            HttpContext http,
            ITeamService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(http, userId => service.ListAsync(
                organizationId, userId, includeArchived, cancellationToken)));

        group.MapGet("/{teamId:guid}", async (
            Guid organizationId,
            Guid teamId,
            HttpContext http,
            ITeamService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(http, async userId =>
                await service.GetAsync(organizationId, userId, teamId, cancellationToken) is { } team
                    ? Results.Ok(team)
                    : Results.NotFound()));

        group.MapPost("", async (
            Guid organizationId,
            CreateTeamRequest request,
            HttpContext http,
            ITeamService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(http, async userId => Results.Created(
                $"/api/core/organizations/{organizationId:D}/teams",
                await service.CreateAsync(organizationId, userId, request, cancellationToken))));

        group.MapPut("/{teamId:guid}", async (
            Guid organizationId,
            Guid teamId,
            UpdateTeamRequest request,
            HttpContext http,
            ITeamService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(http, async userId => Results.Ok(
                await service.UpdateAsync(organizationId, userId, teamId, request, cancellationToken))));

        group.MapPost("/{teamId:guid}/archive", async (
            Guid organizationId,
            Guid teamId,
            TeamRevisionRequest request,
            HttpContext http,
            ITeamService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(http, async userId => Results.Ok(
                await service.ArchiveAsync(organizationId, userId, teamId, request, cancellationToken))));

        group.MapPost("/{teamId:guid}/restore", async (
            Guid organizationId,
            Guid teamId,
            TeamRevisionRequest request,
            HttpContext http,
            ITeamService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(http, async userId => Results.Ok(
                await service.RestoreAsync(organizationId, userId, teamId, request, cancellationToken))));

        group.MapPut("/{teamId:guid}/members/{organizationUserId:guid}", async (
            Guid organizationId,
            Guid teamId,
            Guid organizationUserId,
            UpsertTeamMembershipRequest request,
            HttpContext http,
            ITeamService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(http, async userId => Results.Ok(
                await service.UpsertMemberAsync(
                    organizationId, userId, teamId, organizationUserId, request, cancellationToken))));

        group.MapDelete("/{teamId:guid}/members/{organizationUserId:guid}", async (
            Guid organizationId,
            Guid teamId,
            Guid organizationUserId,
            long expectedRevision,
            HttpContext http,
            ITeamService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(http, async userId => Results.Ok(
                await service.RemoveMemberAsync(
                    organizationId,
                    userId,
                    teamId,
                    organizationUserId,
                    new TeamRevisionRequest(expectedRevision),
                    cancellationToken))));

        return endpoints;
    }

    private static async Task<IResult> ExecuteAsync(
        HttpContext http,
        Func<Guid, Task<IResult>> action)
    {
        var userId = http.User.GetApplicationUserId();
        if (!userId.HasValue) return Results.Unauthorized();
        try
        {
            return await action(userId.Value);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Results.Json(new { errorCode = "not_authorized", message = exception.Message },
                statusCode: StatusCodes.Status403Forbidden);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            return Results.Json(new { errorCode = "revision_conflict", message = exception.Message },
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (KeyNotFoundException exception)
        {
            return Results.NotFound(new { errorCode = "not_found", message = exception.Message });
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { errorCode = "validation_failed", message = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Results.Json(new { errorCode = "conflict", message = exception.Message },
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static Task<IResult> ExecuteAsync<T>(
        HttpContext http,
        Func<Guid, Task<T>> action) =>
        ExecuteAsync(http, async userId => Results.Ok(await action(userId)));
}
