using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Api.Auth;
using CSweet.Application.Compute;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Compute;

public static class ComputeNetworkGrantEndpoints
{
    public sealed record LocalLinkRequest(bool Enabled);
    internal static readonly string[] Actions = [InfrastructureActions.Inbound, InfrastructureActions.PublishPort];
    internal static Guid GrantId(Guid environment, string action) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"explicit-local-link:{environment:N}:{action}")).AsSpan(0, 16));

    public static IEndpointRouteBuilder MapComputeNetworkGrantEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/organizations/{organizationId:guid}/compute/{environmentId:guid}/release", async
            (Guid organizationId, Guid environmentId, HttpContext http, CSweetDbContext db, IComputeBroker broker, CancellationToken ct) =>
        {
            if (http.User.GetApplicationUserId() is not { } user) return Results.Unauthorized();
            try { await ReleaseAsync(db, broker, organizationId, user, environmentId, ct); return Results.NoContent(); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (InvalidOperationException) { return Results.Conflict(new { error = "The resource changed. Refresh Compute before releasing it." }); }
        }).RequireAuthorization();
        endpoints.MapPut("/api/organizations/{organizationId:guid}/compute/{environmentId:guid}/local-link", async
            (Guid organizationId, Guid environmentId, LocalLinkRequest request, HttpContext http, CSweetDbContext db,
                ComputeGrantAdministration grants, CancellationToken ct) =>
        {
            if (http.User.GetApplicationUserId() is not { } user) return Results.Unauthorized();
            try { await SetLocalLinkAsync(db, grants, organizationId, user, environmentId, request.Enabled, ct); return Results.NoContent(); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (InvalidOperationException) { return Results.Conflict(new { error = "The resource or its permissions changed. Refresh Compute." }); }
        }).RequireAuthorization();
        return endpoints;
    }

    internal static async Task ReleaseAsync(CSweetDbContext db, IComputeBroker broker, Guid business, Guid user, Guid environmentId, CancellationToken ct)
    {
        if (!await db.CoreOrganizationUsers.AnyAsync(x => x.OrganizationId == business && x.ApplicationUserId == user &&
            x.EmployeeType == EmployeeType.Human && x.PermissionLevel == OrganizationPermissionLevel.Owner && x.IsActive && x.ArchivedAt == null, ct))
            throw new UnauthorizedAccessException();
        var environment = await db.ComputeEnvironments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == environmentId && x.OrganizationId == business, ct)
            ?? throw new UnauthorizedAccessException();
        if (environment.DesiredState == ComputeDesiredState.Destroyed || environment.TeardownConfirmedAt is not null) return;
        await broker.ChangeLifecycleAsync(business, environment.InstallationId,
            new(environment.Id, environment.Generation, InfrastructureActions.Destroy, $"owner-release:{environment.Id:N}:{environment.Generation}"), ct);
    }

    internal static async Task SetLocalLinkAsync(CSweetDbContext db, ComputeGrantAdministration grants, Guid business,
        Guid user, Guid environmentId, bool enabled, CancellationToken ct)
    {
        if (!await db.CoreOrganizationUsers.AnyAsync(x => x.OrganizationId == business && x.ApplicationUserId == user &&
            x.EmployeeType == EmployeeType.Human && x.PermissionLevel == OrganizationPermissionLevel.Owner && x.IsActive && x.ArchivedAt == null, ct))
            throw new UnauthorizedAccessException();
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var environment = await db.ComputeEnvironments.SingleOrDefaultAsync(x => x.Id == environmentId && x.OrganizationId == business, ct)
            ?? throw new UnauthorizedAccessException();
        if (environment.LeaseExpiresAt <= DateTimeOffset.UtcNow || environment.TeardownConfirmedAt is not null)
            throw new InvalidOperationException();
        var installation = await db.AgentInstallations.Include(x => x.Grant).SingleAsync(x => x.Id == environment.InstallationId, ct);
        var approved = JsonSerializer.Deserialize<HashSet<string>>(installation.Grant?.RequiredCapabilitiesJson ?? "[]") ?? [];
        if (enabled && (!installation.IsEnabled || !Actions.All(approved.Contains))) throw new InvalidOperationException();
        var spec = JsonSerializer.Deserialize<ComputeSpecification>(environment.SpecificationJson, ComputeProtocol.Json)!;
        // Local publishing is confined to guest port 8080 and this environment's lease. It
        // conveys neither an outbound path nor a public listener, and cannot provision another VM.
        var constraints = new ComputeGrantConstraints(1, spec.Resources, 1, spec.LifetimeSeconds,
            [spec.OperatingSystem], [spec.Architecture], [spec.TemplateId], AllowedPublishedPorts: [8080], EnvironmentId: environment.Id);
        foreach (var action in Actions)
        {
            var id = GrantId(environment.Id, action);
            var prior = await db.ScopedActionGrants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            await grants.PutAsync(business, id, new(prior?.Revision ?? 0, environment.InstallationId, environment.WorkstreamId,
                action, constraints, environment.LeaseExpiresAt, enabled), ct);
        }
        var wakeId = Guid.NewGuid();
        db.AgentPlatformEventOutbox.Add(new() { Id = wakeId, OrganizationId = business, TargetInstallationId = environment.InstallationId,
            EventType = "com.csweet.compute.changed.v1", DataJson = JsonSerializer.Serialize(new { environmentId, revision = environment.Revision }),
            IdempotencyKey = $"network-grant:{wakeId:N}", Status = AgentPlatformEventOutboxStatus.Pending,
            OccurredAt = DateTimeOffset.UtcNow, NextAttemptAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
    }
}
