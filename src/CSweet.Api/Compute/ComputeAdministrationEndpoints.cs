using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;
using CSweet.Application.Compute;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Compute;

public static class ComputeAdministrationEndpoints
{
    public sealed record NodeRequest(long ExpectedRevision, string Name, string ProviderId, string KeyId, string PublicKey, bool Enabled);
    public sealed record TemplateRequest(long ExpectedRevision, ComputeTemplate Template);
    public sealed record PlacementRequest(long ExpectedRevision, bool Enabled);

    public static IEndpointRouteBuilder MapComputeAdministrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/compute/administration/{organizationId:guid}").RequireAuthorization("HostAdministration");
        group.AddEndpointFilter(async (invocation, next) =>
        {
            try { return await next(invocation); }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
            catch (InvalidOperationException error) { return Results.Conflict(new { error = error.Message }); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "The registry changed. Refresh and retry with its current revision." }); }
        });
        group.MapPut("/nodes/{nodeId:guid}", async (Guid organizationId, Guid nodeId, NodeRequest request, ComputeRegistry registry, CancellationToken token) =>
            Results.Ok(await registry.PutNodeAsync(organizationId, nodeId, request.ExpectedRevision, request.Name, request.ProviderId,
                request.KeyId, request.PublicKey, request.Enabled, token)));
        group.MapPut("/templates", async (Guid organizationId, TemplateRequest request, ComputeRegistry registry, CancellationToken token) =>
            Results.Ok(await registry.PutTemplateAsync(organizationId, request.Template, request.ExpectedRevision, token)));
        group.MapPut("/templates/{templateId:guid}/nodes/{nodeId:guid}", async (Guid organizationId, Guid templateId, Guid nodeId,
            PlacementRequest request, ComputeRegistry registry, CancellationToken token) =>
            Results.Ok(await registry.PutPlacementAsync(organizationId, templateId, nodeId, request.ExpectedRevision, request.Enabled, token)));
        group.MapGet("/signing-identity", async (IComputeDispatchSigner signer, CancellationToken token) =>
            Results.Ok(await signer.GetIdentityAsync(token)));
        group.MapGet("/nodes", async (Guid organizationId, Guid? afterId, CSweetDbContext db, CancellationToken token) =>
            Results.Ok(await db.ComputeNodes.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
                (afterId == null || x.Id.CompareTo(afterId.Value) > 0)).OrderBy(x => x.Id).Take(100).ToListAsync(token)));
        group.MapGet("/templates", async (Guid organizationId, Guid? afterId, CSweetDbContext db, CancellationToken token) =>
            Results.Ok(await db.ComputeTemplates.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
                (afterId == null || x.Id.CompareTo(afterId.Value) > 0)).OrderBy(x => x.Id).Take(100).ToListAsync(token)));
        group.MapGet("/placements", async (Guid organizationId, Guid? afterId, CSweetDbContext db, CancellationToken token) =>
            Results.Ok(await db.ComputeTemplatePlacements.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
                (afterId == null || x.Id.CompareTo(afterId.Value) > 0)).OrderBy(x => x.Id).Take(100).ToListAsync(token)));
        group.MapPut("/grants/{grantId:guid}", async (Guid organizationId, Guid grantId, ComputeGrantAdministration.Request request,
            [Microsoft.AspNetCore.Mvc.FromServices] ComputeGrantAdministration grants, CancellationToken token) => Results.Ok(await grants.PutAsync(organizationId, grantId, request, token)));
        group.MapGet("/grants/{installationId:guid}", async (Guid organizationId, Guid installationId, CSweetDbContext db, CancellationToken token) =>
            Results.Ok(await db.ScopedActionGrants.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
                x.SubjectKind == CSweet.Domain.Security.GrantSubjectKind.AgentInstallation && x.SubjectId == installationId &&
                (x.Action.StartsWith("compute.") || x.Action.StartsWith("network."))).OrderBy(x => x.Id).Take(100).ToListAsync(token)));
        return endpoints;
    }
}
