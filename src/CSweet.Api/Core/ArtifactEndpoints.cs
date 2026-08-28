using CSweet.Application.Core;
using CSweet.Contracts.Core;
using CSweet.Api.Auth;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Core;

public static class ArtifactEndpoints
{
    public static IEndpointRouteBuilder MapArtifactEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/core/artifacts").RequireAuthorization()
            .AddEndpointFilter<LegacyArtifactAuthorizationFilter>();
        var phaseGroup = endpoints.MapGroup("/api/artifacts").RequireAuthorization()
            .AddEndpointFilter<LegacyArtifactAuthorizationFilter>();

        group.MapGet("/organization/{organizationId:guid}", async (Guid organizationId, IArtifactService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListByOrganizationAsync(organizationId, cancellationToken)));

        group.MapGet("/{id:guid}", async (Guid id, IArtifactService service, CancellationToken cancellationToken) =>
        {
            var artifact = await service.GetAsync(id, cancellationToken);
            return artifact is null ? Results.NotFound() : Results.Ok(artifact);
        });
        phaseGroup.MapGet("/{id:guid}", async (Guid id, IArtifactService service, CancellationToken cancellationToken) =>
        {
            var artifact = await service.GetAsync(id, cancellationToken);
            return artifact is null ? Results.NotFound() : Results.Ok(artifact);
        });

        group.MapPost("/organization/{organizationId:guid}", async (Guid organizationId, CreateArtifactRequest request, IArtifactService service, CancellationToken cancellationToken) =>
        {
            var result = await service.CreateAsync(organizationId, request, cancellationToken);
            return result.Succeeded
                ? Results.Created($"/api/core/artifacts/{result.Artifact!.Id}", result.Artifact)
                : Results.BadRequest(result);
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateArtifactRequest request, IArtifactService service, CancellationToken cancellationToken) =>
        {
            var result = await service.UpdateAsync(id, request, cancellationToken);
            return result.Succeeded
                ? Results.Ok(result.Artifact)
                : Results.BadRequest(result);
        });

        group.MapDelete("/{id:guid}", async (Guid id, IArtifactService service, CancellationToken cancellationToken) =>
        {
            var result = await service.DeleteAsync(id, cancellationToken);
            return result.Succeeded
                ? Results.NoContent()
                : Results.BadRequest(result);
        });

        // Approval endpoints
        group.MapGet("/{artifactId:guid}/approvals", async (Guid artifactId, IArtifactApprovalService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListByArtifactAsync(artifactId, cancellationToken)));

        group.MapPost("/{artifactId:guid}/approve", async (Guid artifactId, CreateApprovalRequest request, IArtifactApprovalService service, CancellationToken cancellationToken) =>
        {
            var result = await service.ApproveAsync(artifactId, request.Comment, cancellationToken);
            return result.Succeeded
                ? Results.Ok(result.Approval)
                : Results.BadRequest(result);
        });
        phaseGroup.MapPost("/{artifactId:guid}/approve", async (Guid artifactId, CreateApprovalRequest request, IArtifactApprovalService service, CancellationToken cancellationToken) =>
        {
            var result = await service.ApproveAsync(artifactId, request.Comment, cancellationToken);
            return result.Succeeded
                ? Results.Ok(result.Approval)
                : Results.BadRequest(result);
        });

        group.MapPost("/{artifactId:guid}/reject", async (Guid artifactId, CreateApprovalRequest request, IArtifactApprovalService service, CancellationToken cancellationToken) =>
        {
            var result = await service.RejectAsync(artifactId, request.Comment, cancellationToken);
            return result.Succeeded
                ? Results.Ok(result.Approval)
                : Results.BadRequest(result);
        });
        phaseGroup.MapPost("/{artifactId:guid}/reject", async (Guid artifactId, CreateApprovalRequest request, IArtifactApprovalService service, CancellationToken cancellationToken) =>
        {
            var result = await service.RejectAsync(artifactId, request.Comment, cancellationToken);
            return result.Succeeded
                ? Results.Ok(result.Approval)
                : Results.BadRequest(result);
        });

        group.MapPost("/{artifactId:guid}/request-revision", async (Guid artifactId, CreateApprovalRequest request, IArtifactApprovalService service, CancellationToken cancellationToken) =>
        {
            var result = await service.RequestRevisionAsync(artifactId, request.Comment, cancellationToken);
            return result.Succeeded
                ? Results.Ok(result.Approval)
                : Results.BadRequest(result);
        });

        return endpoints;
    }
}

/// <summary>Stops the compatibility API from bypassing document discovery and exact sharing rules.</summary>
public sealed class LegacyArtifactAuthorizationFilter(CSweetDbContext db) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var applicationUserId = context.HttpContext.User.GetApplicationUserId();
        if (!applicationUserId.HasValue) return Results.Unauthorized();
        Guid? organizationId = context.HttpContext.Request.RouteValues.TryGetValue("organizationId", out var organizationValue) &&
                               Guid.TryParse(organizationValue?.ToString(), out var parsedOrganization)
            ? parsedOrganization : null;
        if (!organizationId.HasValue)
        {
            var key = context.HttpContext.Request.RouteValues.TryGetValue("artifactId", out var artifactValue)
                ? artifactValue : context.HttpContext.Request.RouteValues.GetValueOrDefault("id");
            if (Guid.TryParse(key?.ToString(), out var artifactId))
                organizationId = await db.CoreArtifacts.AsNoTracking().Where(x => x.Id == artifactId)
                    .Select(x => (Guid?)x.OrganizationId).SingleOrDefaultAsync(context.HttpContext.RequestAborted);
        }
        if (!organizationId.HasValue) return Results.NotFound();
        var authorized = await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
            x.OrganizationId == organizationId && x.ApplicationUserId == applicationUserId && x.IsActive &&
            x.PermissionLevel >= OrganizationPermissionLevel.Manager, context.HttpContext.RequestAborted);
        return authorized ? await next(context) : Results.Forbid();
    }
}
