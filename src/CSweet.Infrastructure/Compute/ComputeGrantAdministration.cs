using System.Text.Json;
using CSweet.Application.Compute;
using CSweet.Application.Setup;
using CSweet.Compute.Contracts;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Compute;

/// <summary>Called only by the host-administration API. Manifest permissions remain a separate approval.</summary>
public sealed class ComputeGrantAdministration(CSweetDbContext db, TimeProvider clock, IAuditExecutionContextAccessor context)
{
    public sealed record Request(long ExpectedRevision, Guid InstallationId, Guid WorkstreamId, string Action,
        ComputeGrantConstraints Constraints, DateTimeOffset ExpiresAt, bool Enabled = true);

    public async Task<ScopedActionGrant> PutAsync(Guid organizationId, Guid grantId, Request request, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        if (grantId == Guid.Empty || request.ExpectedRevision < 0 || request.InstallationId == Guid.Empty || request.WorkstreamId == Guid.Empty ||
            request.Action is not (InfrastructureActions.Provision or InfrastructureActions.Read or InfrastructureActions.List or
                InfrastructureActions.Execute or InfrastructureActions.Start or InfrastructureActions.Stop or InfrastructureActions.Restart or
                InfrastructureActions.Destroy or InfrastructureActions.Inbound or InfrastructureActions.PublishPort or
                InfrastructureActions.Outbound or InfrastructureActions.PrivateNetwork or InfrastructureActions.PublicEndpoint) ||
            request.Constraints is not { Version: 1, MaximumResources.IsValid: true, MaximumConcurrentEnvironments: > 0, MaximumLifetimeSeconds: >= 0 } ||
            request.Constraints.OperatingSystems is not { Count: > 0 } || request.Constraints.Architectures is not { Count: > 0 } ||
            request.Constraints.Templates is not { Count: > 0 } || request.ExpiresAt <= now ||
            (request.ExpiresAt > now.AddDays(30) && !(request.Constraints.MaximumLifetimeSeconds == 0 && request.ExpiresAt == DateTimeOffset.MaxValue)))
            throw new ArgumentException("A supported action and valid resource constraints are required. Use explicit until-release constraints for a non-expiring grant.");
        var business = organizationId.ToString("D");
        if (!await db.AgentInstallations.AnyAsync(x => x.Id == request.InstallationId && x.BusinessId == business, token) ||
            !await db.Workstreams.AnyAsync(x => x.Id == request.WorkstreamId && x.OrganizationId == organizationId, token))
            throw new ArgumentException("Installation and workstream must belong to the organization.");
        var grant = await db.ScopedActionGrants.SingleOrDefaultAsync(x => x.Id == grantId, token);
        if (grant is null)
        {
            if (request.ExpectedRevision != 0) throw new InvalidOperationException("Grant revision changed.");
            db.ScopedActionGrants.Add(grant = new() { Id = grantId, OrganizationId = organizationId,
                SubjectKind = GrantSubjectKind.AgentInstallation, SubjectId = request.InstallationId, ScopeKind = GrantScopeKind.Workstream,
                ScopeId = request.WorkstreamId, Action = request.Action, GrantedAt = now, GrantedBySubjectKind = GrantSubjectKind.AutomationIdentity });
        }
        else
        {
            if (grant.OrganizationId != organizationId || grant.SubjectKind != GrantSubjectKind.AgentInstallation ||
                grant.SubjectId != request.InstallationId || grant.ScopeKind != GrantScopeKind.Workstream || grant.ScopeId != request.WorkstreamId ||
                grant.Action != request.Action || grant.ParentGrantId is not null || grant.Revision != request.ExpectedRevision)
                throw new InvalidOperationException("Grant scope or revision changed.");
            grant.Revision++;
        }
        grant.ConstraintsJson = JsonSerializer.Serialize(request.Constraints, ComputeProtocol.Json);
        grant.ExpiresAt = request.ExpiresAt; grant.RevokedAt = request.Enabled ? null : now;
        var id = Guid.NewGuid();
        db.AuditOutbox.Add(new() { Id = id, CreatedAt = now, RequestJson = JsonSerializer.Serialize(new AuditEventWriteRequest(
            "compute.grant.changed.v1", Category: "Infrastructure", Outcome: request.Enabled ? "Granted" : "Revoked", OrganizationId: organizationId,
            EntityType: "ScopedActionGrant", EntityId: grantId, Actor: context.Current?.Actor ?? new("HostAdministrator"), OccurredAt: now, EventId: id,
            UseAmbientOrganization: false, MetadataJson: JsonSerializer.Serialize(new { request.InstallationId, request.WorkstreamId, request.Action, grant.Revision, request.ExpiresAt }))) });
        await db.SaveChangesAsync(token);
        return grant;
    }
}
