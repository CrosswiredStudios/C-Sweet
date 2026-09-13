using System.Text.Json;
using CSweet.Application.Compute;
using CSweet.Application.Setup;
using CSweet.Compute.Contracts;
using CSweet.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Compute;

public sealed partial class ComputeBroker
{
    public async Task<ComputeEnvironmentView> ReadAsync(Guid organizationId, Guid installationId, Guid environmentId, CancellationToken token)
    {
        ComputeEnvironmentView view;
        Guid workstreamId;
        IReadOnlyList<ComputeActionAuthorization> authority;
        try
        {
            var environment = await db.ComputeEnvironments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == environmentId &&
                x.OrganizationId == organizationId && x.InstallationId == installationId, token)
                ?? throw new UnauthorizedAccessException("The compute environment is unavailable.");
            workstreamId = environment.WorkstreamId;
            await RequireActorAsync(organizationId, installationId, workstreamId, InfrastructureActions.Read, token);
            authority = AccessAuthority(await GrantsAsync(organizationId, installationId, workstreamId, token), InfrastructureActions.Read);
            view = View(environment);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or ArgumentException)
        {
            await RejectAccessAsync(organizationId, installationId, InfrastructureActions.Read, error, token);
            throw;
        }
        await RecordAccessAsync(organizationId, installationId, workstreamId, InfrastructureActions.Read, [view], authority, token);
        return view;
    }

    public async Task<ComputeEnvironmentPage> ListAsync(Guid organizationId, Guid installationId, Guid workstreamId,
        Guid? afterId, int limit, CancellationToken token)
    {
        ComputeEnvironmentPage page;
        IReadOnlyList<ComputeActionAuthorization> authority;
        try
        {
            if (limit is < 1 or > 100 || afterId == Guid.Empty) throw new ArgumentException("Use a page size from one to one hundred.");
            await RequireActorAsync(organizationId, installationId, workstreamId, InfrastructureActions.List, token);
            authority = AccessAuthority(await GrantsAsync(organizationId, installationId, workstreamId, token), InfrastructureActions.List);
            var query = db.ComputeEnvironments.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
                x.InstallationId == installationId && x.WorkstreamId == workstreamId);
            if (afterId is { } after) query = query.Where(x => x.Id.CompareTo(after) > 0);
            var rows = await query.OrderBy(x => x.Id).Take(limit + 1).ToListAsync(token);
            page = new(rows.Take(limit).Select(View).ToArray(), rows.Count > limit ? rows[limit - 1].Id : null);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or ArgumentException)
        {
            await RejectAccessAsync(organizationId, installationId, InfrastructureActions.List, error, token);
            throw;
        }
        await RecordAccessAsync(organizationId, installationId, workstreamId, InfrastructureActions.List, page.Items, authority, token);
        return page;
    }

    private IReadOnlyList<ComputeActionAuthorization> AccessAuthority(IEnumerable<ScopedActionGrant> grants, string action)
    {
        var now = clock.GetUtcNow();
        RequireAction(grants, action, now);
        // One sufficient current grant authorizes a read. Record that exact revision, not unrelated grants.
        var grant = grants.First(x => x.Action == action && x.Id != Guid.Empty && x.Revision > 0 && x.ExpiresAt > now);
        return [new(grant.Id, grant.Revision, action, grant.ExpiresAt!.Value)];
    }

    private async Task RecordAccessAsync(Guid organizationId, Guid installationId, Guid workstreamId, string action,
        IReadOnlyList<ComputeEnvironmentView> items, IReadOnlyList<ComputeActionAuthorization> authority, CancellationToken token)
    {
        // Commit evidence before disclosure. Ledger failure does not release a response or mutate compute state.
        await audit.AppendAsync(new AuditEventWriteRequest("compute.access.v1", Category: "Infrastructure", Outcome: "Accepted",
            OrganizationId: organizationId, EntityType: "ComputeEnvironment", EntityId: action == InfrastructureActions.Read ? items[0].Id : null,
            Actor: new("Agent", InstallationId: installationId), OccurredAt: clock.GetUtcNow(), UseAmbientOrganization: false,
            MetadataJson: JsonSerializer.Serialize(new { action, workstreamId, grants = authority,
                items = items.Select(x => new { x.Id, x.Revision, x.Generation }).ToArray() })), token);
    }

    private async Task RejectAccessAsync(Guid organizationId, Guid installationId, string action, Exception error, CancellationToken token)
    {
        // Caller-supplied scope is unverified. Keep rejection in the system stream, without target/provider details.
        await audit.AppendAsync(new AuditEventWriteRequest("compute.access.rejected.v1", Category: "Infrastructure", Outcome: "Rejected",
            Actor: new("Agent", IdentityVerified: false, InstallationId: installationId), OccurredAt: clock.GetUtcNow(),
            UseAmbientOrganization: false, ErrorCode: error is UnauthorizedAccessException ? "authority_denied" : "request_rejected",
            MetadataJson: JsonSerializer.Serialize(new { action, requestedOrganizationId = organizationId })), token);
    }
}
