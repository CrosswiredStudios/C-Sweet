using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Compute;
using CSweet.Application.Setup;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CSweet.Infrastructure.Compute;

/// <summary>Application policy for the local, ephemeral Linux workspace. Agents cannot select provider settings.</summary>
public sealed class ComputeDefaultsService(CSweetDbContext db, TimeProvider clock,
    ComputeGrantAdministration grants, IConfiguration? configuration = null) : IComputeDefaults
{
    private static readonly string[] Actions = [InfrastructureActions.Provision, InfrastructureActions.Read,
        InfrastructureActions.List, InfrastructureActions.Execute, InfrastructureActions.Start, InfrastructureActions.Stop,
        InfrastructureActions.Restart, InfrastructureActions.Destroy];

    public async Task EnsureRequestedAsync(Guid installationId, CancellationToken token, bool retryFailed = false)
    {
        var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.Grant)
            .SingleOrDefaultAsync(x => x.Id == installationId && x.IsEnabled &&
                x.RevisionStatus == PluginRevisionStatus.Active && x.SetupState == PluginSetupState.Ready, token);
        if (installation is null || !Guid.TryParse(installation.BusinessId, out var organizationId) ||
            !Approved(installation).Contains(InfrastructureActions.Provision)) return;
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == installationId && x.IsActive &&
            x.ArchivedAt == null && x.EmployeeType == EmployeeType.Agent, token);
        if (actor is null) return;
        var existingAccess = await db.Set<ComputeAgentAccess>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == installationId, token);
        if (existingAccess is not null)
        {
            if (retryFailed)
            {
                var prior = await db.Set<ComputeLocalSetup>().SingleAsync(x => x.Id == existingAccess.SetupId, token);
                if (prior.State == "Failed")
                {
                    prior.State = "Pending"; prior.ErrorCode = null; prior.HandoffHash = null; prior.HandoffExpiresAt = null;
                    prior.Revision++; prior.UpdatedAt = clock.GetUtcNow(); await db.SaveChangesAsync(token);
                }
            }
            return;
        }
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token) : null;
        var setup = await db.Set<ComputeLocalSetup>().SingleOrDefaultAsync(x => x.OrganizationId == organizationId, token);
        var now = clock.GetUtcNow();
        if (setup is null)
            db.Add(setup = new ComputeLocalSetup { Id = Guid.NewGuid(), OrganizationId = organizationId, CreatedAt = now, UpdatedAt = now });
        var workstreamId = StableId($"compute-workspace:{installationId:D}");
        db.Workstreams.Add(new Workstream { Id = workstreamId, OrganizationId = organizationId,
            AccountableManagerOrganizationUserId = actor.Id, Name = "Application testing",
            Outcome = "Run and test software in isolated, temporary compute environments.", LifecycleStage = "Active",
            CreatedAt = now, UpdatedAt = now });
        db.Add(new ComputeAgentAccess { Id = installationId, OrganizationId = organizationId, SetupId = setup.Id,
            WorkstreamId = workstreamId, CreatedAt = now });
        var auditId = Guid.NewGuid();
        db.ComputeAuditOutbox.Add(new() { Id = auditId, CreatedAt = now, RequestJson = JsonSerializer.Serialize(
            new AuditEventWriteRequest("compute.workspace.preparation-requested.v1", Category: "Infrastructure",
                Outcome: "Accepted", OrganizationId: organizationId, EntityType: "AgentInstallation", EntityId: installationId,
                Actor: new("Application", InstallationId: installationId), EventId: auditId, OccurredAt: now,
                UseAmbientOrganization: false)) });
        await db.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
    }

    public async Task<ComputeDefaults> ReadAsync(Guid organizationId, Guid installationId, CancellationToken token)
    {
        var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.Grant).SingleOrDefaultAsync(x =>
            x.Id == installationId && x.BusinessId == organizationId.ToString("D") && x.IsEnabled &&
            x.RevisionStatus == PluginRevisionStatus.Active && x.SetupState == PluginSetupState.Ready, token);
        if (installation is null || !Approved(installation).Contains(InfrastructureActions.Read)) throw new UnauthorizedAccessException();
        var access = await db.Set<ComputeAgentAccess>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == installationId && x.OrganizationId == organizationId, token);
        if (access is null) return new("Pending", null, null, null);
        var setup = await db.Set<ComputeLocalSetup>().AsNoTracking().SingleAsync(x => x.Id == access.SetupId, token);
        if (setup.State == "Ready") await ActivateAccessAsync(setup, token);
        if (setup.State == "Ready" && Approved(installation).Contains("source-control.personal-work.prepare.v1"))
        {
            var template = await db.ComputeTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.TemplateId == setup.TemplateId, token);
            if (template is null || JsonSerializer.Deserialize<ComputeTemplate>(template.TemplateJson, ComputeProtocol.Json)?.Features.Contains("docker") != true)
                return new("Pending", access.WorkstreamId, null, "docker_preparation_pending");
        }
        return new(setup.State == "Ready" && access.GrantsCreatedAt is null ? "Pending" : setup.State, access.WorkstreamId, setup.State == "Ready" ? setup.TemplateId : null, setup.ErrorCode);
    }

    public async Task ActivateAccessAsync(ComputeLocalSetup setup, CancellationToken token)
    {
        if (setup.State != "Ready" || setup.TemplateId is null) throw new InvalidOperationException("Compute is not ready.");
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token) : null;
        var accesses = await db.Set<ComputeAgentAccess>().Where(x => x.SetupId == setup.Id).ToListAsync(token);
        foreach (var access in accesses)
        {
            var policyChanged = false;
            // Retire only untouched grants from the previous automatic network defaults.
            foreach (var action in new[] { InfrastructureActions.Inbound, InfrastructureActions.PublishPort })
            {
                var oldId = StableId($"compute-default-grant:{access.Id:D}:{action}");
                var legacy = await db.ScopedActionGrants.SingleOrDefaultAsync(x => x.Id == oldId && x.Revision == 1 && x.RevokedAt == null, token);
                if (legacy is not null)
                {
                    legacy.RevokedAt = clock.GetUtcNow(); legacy.Revision++;
                    var auditId = Guid.NewGuid();
                    db.ComputeAuditOutbox.Add(new() { Id = auditId, CreatedAt = clock.GetUtcNow(), RequestJson = JsonSerializer.Serialize(
                        new AuditEventWriteRequest("compute.automatic-network-grant.retired.v1", Category: "Infrastructure", Outcome: "Revoked",
                            OrganizationId: access.OrganizationId, EntityType: "ScopedActionGrant", EntityId: oldId,
                            Actor: new("Application"), EventId: auditId, UseAmbientOrganization: false)) });
                }
            }
            var installation = await db.AgentInstallations.Include(x => x.Grant).SingleAsync(x => x.Id == access.Id, token);
            if (!installation.IsEnabled || installation.RevisionStatus != PluginRevisionStatus.Active) continue;
            var approved = Approved(installation);
            var now = clock.GetUtcNow();
            var lifetime = configuration?.GetValue<int?>("CSweet:Compute:Defaults:MaximumLifetimeSeconds") ?? 0;
            if (lifetime < 0) throw new InvalidOperationException("Compute maximum lifetime must be zero (until released) or positive.");
            var constraints = new ComputeGrantConstraints(1, new(2, 2048, 20480), 1, lifetime, ["linux"], ["x64"],
                [setup.TemplateId], AllowedPublishedPorts: [8080]);
            foreach (var action in Actions.Where(approved.Contains))
            {
                var id = StableId($"compute-default-grant:{access.Id:D}:{action}");
                // Never replace or revive a revoked/edited grant. These defaults are created once.
                var prior = await db.ScopedActionGrants.SingleOrDefaultAsync(x => x.Id == id, token);
                if (prior is not null)
                {
                    // Upgrade only the known, unmodified legacy automatic policy. Never touch
                    // network grants, revoked/expired grants, or administrator-customized terms.
                    var legacy = JsonSerializer.Deserialize<ComputeGrantConstraints>(prior.ConstraintsJson, ComputeProtocol.Json);
                    if (lifetime == 0 && prior.Revision <= 2 && prior.RevokedAt is null && prior.ExpiresAt > now &&
                        Math.Abs((prior.ExpiresAt.Value - prior.GrantedAt).TotalDays - 30) < 0.001 &&
                        legacy is { Version: 1, MaximumLifetimeSeconds: 3600, MaximumConcurrentEnvironments: 1,
                            AllowPersistent: false, AllowOutbound: false, AllowPublicEndpoint: false, EnvironmentId: null } &&
                        legacy.MaximumResources == constraints.MaximumResources &&
                        legacy.OperatingSystems.SetEquals(constraints.OperatingSystems) && legacy.Architectures.SetEquals(constraints.Architectures) &&
                        legacy.Templates.SetEquals(constraints.Templates) && legacy.AllowedPublishedPorts?.SetEquals([8080]) == true)
                    {
                        await grants.PutAsync(access.OrganizationId, id, new(prior.Revision, access.Id, access.WorkstreamId, action,
                            constraints, DateTimeOffset.MaxValue), token);
                        policyChanged = true;
                        continue;
                    }
                    if (access.GrantsCreatedAt is not null) continue;
                    // Replace a template pin only for an untouched automatic default. Preserve
                    // administrator revisions, expiration and revocation across image upgrades.
                    if (prior.Revision == 1 && prior.RevokedAt is null && prior.ExpiresAt > now)
                        await grants.PutAsync(access.OrganizationId, id, new(prior.Revision, access.Id, access.WorkstreamId, action,
                            constraints, prior.ExpiresAt.Value), token);
                    continue;
                }
                if (access.GrantsCreatedAt is not null) continue;
                await grants.PutAsync(access.OrganizationId, id, new(0, access.Id, access.WorkstreamId, action,
                    constraints, lifetime == 0 ? DateTimeOffset.MaxValue : now.AddDays(30)), token);
            }
            if (access.GrantsCreatedAt is not null && !policyChanged) continue;
            access.GrantsCreatedAt = now;
            var eventId = Guid.NewGuid();
            db.AgentPlatformEventOutbox.Add(new() { Id = eventId, OrganizationId = access.OrganizationId,
                TargetInstallationId = access.Id, EventType = "com.csweet.compute.available.v1",
                DataJson = JsonSerializer.Serialize(new { revision = setup.Revision }),
                IdempotencyKey = $"compute-available:{access.Id:D}:{setup.Revision}:{(policyChanged ? "until-release" : "initial")}", Status = AgentPlatformEventOutboxStatus.Pending,
                OccurredAt = now, NextAttemptAt = now });
        }
        await db.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
    }

    private static HashSet<string> Approved(AgentInstallation installation) =>
        JsonSerializer.Deserialize<HashSet<string>>(installation.Grant?.RequiredCapabilitiesJson ?? "[]") ?? [];
    private static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
}
