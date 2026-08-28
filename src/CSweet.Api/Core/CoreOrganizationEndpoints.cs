using CSweet.Application.Core;
using CSweet.Contracts.Core;
using CSweet.Api.Auth;
using System.Security.Claims;

namespace CSweet.Api.Core;

public static class CoreOrganizationEndpoints
{
    public static IEndpointRouteBuilder MapCoreOrganizationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/core/organizations");
        var phaseGroup = endpoints.MapGroup("/api/organizations");

        group.MapGet("", async (ICoreOrganizationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(cancellationToken)));
        phaseGroup.MapGet("", async (ICoreOrganizationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(cancellationToken)));

        group.MapGet("/{id:guid}", async (Guid id, ICoreOrganizationService service, CancellationToken cancellationToken) =>
        {
            var org = await service.GetAsync(id, cancellationToken);
            return org is null ? Results.NotFound() : Results.Ok(org);
        });
        phaseGroup.MapGet("/{id:guid}", async (Guid id, ICoreOrganizationService service, CancellationToken cancellationToken) =>
        {
            var org = await service.GetAsync(id, cancellationToken);
            return org is null ? Results.NotFound() : Results.Ok(org);
        });

        group.MapPost("", async (CreateOrganizationRequest request, ClaimsPrincipal principal, ICoreOrganizationService service, CancellationToken cancellationToken) =>
        {
            var result = await service.CreateAsync(request, cancellationToken, principal.GetApplicationUserId());
            return result.Succeeded
                ? Results.Created($"/api/core/organizations/{result.Organization!.Id}", result.Organization)
                : Results.BadRequest(result);
        });
        phaseGroup.MapPost("", async (CreateOrganizationRequest request, ClaimsPrincipal principal, ICoreOrganizationService service, CancellationToken cancellationToken) =>
        {
            var result = await service.CreateAsync(request, cancellationToken, principal.GetApplicationUserId());
            return result.Succeeded
                ? Results.Created($"/api/organizations/{result.Organization!.Id}", result.Organization)
                : Results.BadRequest(result);
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateOrganizationRequest request, ICoreOrganizationService service, CancellationToken cancellationToken) =>
        {
            var result = await service.UpdateAsync(id, request, cancellationToken);
            return result.Succeeded
                ? Results.Ok(result.Organization)
                : Results.BadRequest(result);
        });
        phaseGroup.MapPut("/{id:guid}", async (Guid id, UpdateOrganizationRequest request, ICoreOrganizationService service, CancellationToken cancellationToken) =>
        {
            var result = await service.UpdateAsync(id, request, cancellationToken);
            return result.Succeeded
                ? Results.Ok(result.Organization)
                : Results.BadRequest(result);
        });

        group.MapDelete("/{id:guid}", async (Guid id, ICoreOrganizationService service, CancellationToken cancellationToken) =>
        {
            var result = await service.DeleteAsync(id, cancellationToken);
            if (result.Succeeded) return Results.NoContent();
            return result.ErrorCode == "not_found"
                ? Results.NotFound(result)
                : Results.Conflict(result);
        });

        return endpoints;
    }
}
