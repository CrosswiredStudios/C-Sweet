using System.Text.Json;
using CSweet.Api.Auth;
using CSweet.Application.Compute;
using CSweet.Application.Setup;
using CSweet.Contracts.Compute;
using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Compute;

public static class ComputeManagementEndpoints
{
    public static IEndpointRouteBuilder MapComputeManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/compute").RequireAuthorization();
        group.MapPost("/setup", async (Guid organizationId, HttpContext http, CSweetDbContext db, CancellationToken token) =>
        {
            if (http.User.GetApplicationUserId() is not { } user) return Results.Unauthorized();
            try { await StartSetupAsync(db, organizationId, user, token); return Results.Accepted(); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });
        group.MapGet("/creation-options", async (Guid organizationId, HttpContext http, CSweetDbContext db,
            ComputeDefaultsService defaults, IConfiguration configuration, CancellationToken token) =>
        {
            if (http.User.GetApplicationUserId() is not { } user) return Results.Unauthorized();
            try
            {
                await RequireOwnerAsync(db, organizationId, user, token);
                var lifetime = configuration.GetValue<int>("CSweet:Compute:Defaults:MaximumLifetimeSeconds");
                if (lifetime < 0) return Results.Conflict();
                return Results.Ok(new ComputeCreationOptions(await defaults.ListCreationOptionsAsync(organizationId, token), lifetime));
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });
        group.MapPost("/instances", async (Guid organizationId, CreateLinuxInstance request, HttpContext http, CSweetDbContext db,
            ComputeDefaultsService defaults, IComputeBroker broker, CancellationToken token) =>
        {
            if (http.User.GetApplicationUserId() is not { } user) return Results.Unauthorized();
            try { return Results.Ok(await CreateAsync(db, defaults, broker, organizationId, user, request, token)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "Use a name starting with a lowercase letter, containing only lowercase letters, numbers and hyphens (up to 64 characters), and an allowed lifetime." }); }
            catch (InvalidOperationException) { return Results.Conflict(new { error = "Compute availability, the instance name, capacity or project permissions changed. Refresh Compute and check the agent's existing instances." }); }
        });
        return endpoints;
    }

    internal static async Task RequireOwnerAsync(CSweetDbContext db, Guid business, Guid user, CancellationToken token)
    {
        if (!await db.CoreOrganizationUsers.AnyAsync(member => member.OrganizationId == business && member.ApplicationUserId == user &&
            member.EmployeeType == EmployeeType.Human && member.PermissionLevel == OrganizationPermissionLevel.Owner &&
            member.IsActive && member.ArchivedAt == null, token)) throw new UnauthorizedAccessException();
    }

    internal static async Task StartSetupAsync(CSweetDbContext db, Guid business, Guid user, CancellationToken token)
    {
        await RequireOwnerAsync(db, business, user, token);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token) : null;
        await new CSweet.Infrastructure.Core.ProjectWorkPolicy(db, TimeProvider.System).LockAsync(business, token);
        if (!await db.Set<ComputeLocalSetup>().AnyAsync(setup => setup.OrganizationId == business, token))
        {
            var now = DateTimeOffset.UtcNow;
            var setup = new ComputeLocalSetup { Id = Guid.NewGuid(), OrganizationId = business, CreatedAt = now, UpdatedAt = now };
            db.Add(setup);
            QueueOwnerAudit(db, business, user, setup.Id, "compute.workspace.preparation-requested.v1");
            await db.SaveChangesAsync(token);
        }
        if (transaction is not null) await transaction.CommitAsync(token);
    }

    internal static async Task<ComputeEnvironmentView> CreateAsync(CSweetDbContext db, ComputeDefaultsService defaults,
        IComputeBroker broker, Guid business, Guid user, CreateLinuxInstance request, CancellationToken token)
    {
        await RequireOwnerAsync(db, business, user, token);
        if (request.RequestId == Guid.Empty || request.InstallationId == Guid.Empty || request.ProjectId == Guid.Empty ||
            !ComputeSpecification.Identifier(request.Name) || request.LifetimeSeconds < 0) throw new ArgumentException();
        var policy = await defaults.ReadProjectAsync(business, request.InstallationId, request.ProjectId, token);
        if (policy.State != "Ready" || policy.TemplateId is null) throw new InvalidOperationException();
        if (policy.MaximumLifetimeSeconds > 0 && (request.LifetimeSeconds == 0 || request.LifetimeSeconds > policy.MaximumLifetimeSeconds))
            throw new ArgumentException();
        QueueOwnerAudit(db, business, user, request.InstallationId, "compute.instance.owner-requested.v1");
        return await broker.RequestAsync(business, request.InstallationId,
            new(request.ProjectId, request.Name, $"owner:{user:N}:{request.RequestId:N}",
                new("linux", "x64", policy.TemplateId, new(2, 2048, 20480), request.LifetimeSeconds)), token);
    }

    internal static void QueueOwnerAudit(CSweetDbContext db, Guid business, Guid user, Guid entity, string eventType)
    {
        var eventId = Guid.NewGuid();
        db.AuditOutbox.Add(new()
        {
            Id = eventId, CreatedAt = DateTimeOffset.UtcNow,
            RequestJson = JsonSerializer.Serialize(new AuditEventWriteRequest(eventType, Category: "Infrastructure", Outcome: "Requested",
                OrganizationId: business, EntityType: "Compute", EntityId: entity, Actor: new("Human", ApplicationUserId: user),
                EventId: eventId, OccurredAt: DateTimeOffset.UtcNow, UseAmbientOrganization: false))
        });
    }
}
