using System.Text.Json;
using CSweet.Application.Compute;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Compute;

public sealed partial class ComputeBroker
{
    private async Task<bool> EnsureLocalLinksAsync(ComputeEnvironment environment, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        if (environment.LeaseExpiresAt <= now || environment.TeardownConfirmedAt is not null ||
            environment.DesiredState == ComputeDesiredState.Destroyed ||
            environment.State is ComputeLifecycleState.Destroyed or ComputeLifecycleState.Destroying) return false;
        var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.Grant).Include(x => x.PackageVersion)
            .SingleAsync(x => x.Id == environment.InstallationId, token);
        if (!ComputeLocalLinkGrants.IsApproved(installation)) return false;
        foreach (var action in ComputeLocalLinkGrants.Actions)
            await RequireActorAsync(environment.OrganizationId, environment.InstallationId, environment.WorkstreamId, action, token, environment.Id);
        var spec = JsonSerializer.Deserialize<ComputeSpecification>(environment.SpecificationJson, Json)!;
        var constraints = new ComputeGrantConstraints(1, spec.Resources, 1, spec.LifetimeSeconds,
            [spec.OperatingSystem], [spec.Architecture], [spec.TemplateId], AllowedPublishedPorts: [8080], EnvironmentId: environment.Id);
        var changed = false;
        foreach (var action in ComputeLocalLinkGrants.Actions)
        {
            var id = ComputeLocalLinkGrants.GrantId(environment.Id, action);
            // Never revive a revoked grant or overwrite owner edits, even after an agent upgrade.
            if (await db.ScopedActionGrants.AnyAsync(x => x.Id == id, token)) continue;
            db.ScopedActionGrants.Add(new() { Id = id, OrganizationId = environment.OrganizationId,
                SubjectKind = GrantSubjectKind.AgentInstallation, SubjectId = environment.InstallationId,
                ScopeKind = GrantScopeKind.Workstream, ScopeId = environment.WorkstreamId, Action = action,
                GrantedBySubjectKind = GrantSubjectKind.AutomationIdentity, GrantedBySubjectId = environment.InstallationId,
                GrantedAt = now, ExpiresAt = environment.LeaseExpiresAt,
                ConstraintsJson = JsonSerializer.Serialize(constraints, Json) });
            changed = true;
        }
        if (!changed) return false;
        var auditId = Guid.NewGuid();
        db.AuditOutbox.Add(new() { Id = auditId, CreatedAt = now, RequestJson = JsonSerializer.Serialize(
            new CSweet.Application.Setup.AuditEventWriteRequest("compute.local-test-links.granted.v1", Category: "Infrastructure",
                Outcome: "Granted", OrganizationId: environment.OrganizationId, EntityType: "ComputeEnvironment", EntityId: environment.Id,
                Actor: new("Application", InstallationId: environment.InstallationId), EventId: auditId, OccurredAt: now,
                UseAmbientOrganization: false)) });
        var wakeId = Guid.NewGuid();
        db.AgentPlatformEventOutbox.Add(new() { Id = wakeId, OrganizationId = environment.OrganizationId,
            TargetInstallationId = environment.InstallationId, EventType = "com.csweet.compute.changed.v1",
            DataJson = JsonSerializer.Serialize(new { environmentId = environment.Id, revision = environment.Revision }),
            IdempotencyKey = $"hired-local-link:{wakeId:N}", Status = AgentPlatformEventOutboxStatus.Pending, OccurredAt = now, NextAttemptAt = now });
        return true;
    }
}
