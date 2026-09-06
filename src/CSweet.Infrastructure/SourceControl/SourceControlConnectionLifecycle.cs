using CSweet.Application.Setup;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.SourceControl;

public sealed partial class InternalRepositoryManagementService
{
    public async Task<SourceControlConnectionDisconnectPlan> ConnectionDisconnectPlanAsync(Guid business, Guid user, Guid id, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, true, ct);
        return await DisconnectPlanAsync(business, await ConnectionRecordAsync(business, id, ct), ct);
    }

    private async Task<SourceControlConnectionDisconnectPlan> DisconnectPlanAsync(Guid business, SourceControlConnection connection, CancellationToken ct)
    {
        var id = connection.Id; var blockers = new List<string>();
        if (connection.Provider != SourceControlProvider.GitHub) blockers.Add("Only unused GitHub connections can be disconnected here. Internal Git remains available for offline projects.");
        if (connection.Status == SourceControlConnectionStatus.Disconnected) blockers.Add("This connection is already disconnected.");
        if (await db.SourceControlRepositories.AnyAsync(r => r.OrganizationId == business && r.ConnectionId == id, ct))
            blockers.Add("Repositories are attached, including any archived repositories. Keep this connection to preserve their access and work history.");
        if (await db.SourceControlRepositoryTemplates.AnyAsync(t => t.OrganizationId == business && t.ConnectionId == id, ct) ||
            await db.RepositoryProvisioningPolicies.AnyAsync(p => p.OrganizationId == business && p.ConnectionId == id, ct))
            blockers.Add("Repository templates or provisioning policies still use this connection.");
        if (await db.RepositoryProvisioningRequests.AnyAsync(r => r.OrganizationId == business && r.ConnectionId == id, ct))
            blockers.Add("Repository provisioning requests or provisioning history still use this connection.");
        if (await db.SourceControlOnboardingSessions.AnyAsync(s => s.OrganizationId == business && s.ConnectionId == id &&
            (s.Status == SourceControlOnboardingStatus.InProgress || s.Status == SourceControlOnboardingStatus.AwaitingProvider), ct))
            blockers.Add("An unfinished setup session uses this connection. Finish setup first.");
        var dependencyBlockers = new List<string>();
        if (connection.Provider != SourceControlProvider.GitHub || connection.Status == SourceControlConnectionStatus.Disconnected)
            dependencyBlockers.Add("Only a connected GitHub connection can be disconnected with retained repositories.");
        if (await db.SourceControlWorkspaces.AnyAsync(w => w.OrganizationId == business && w.Repository!.ConnectionId == id &&
            w.Status != SourceControlWorkspaceStatus.Removed && w.Status != SourceControlWorkspaceStatus.Failed, ct))
            dependencyBlockers.Add("Clean up active workspaces before disconnecting this connection.");
        var repositoryIds = db.SourceControlRepositories.Where(r => r.OrganizationId == business && r.ConnectionId == id).Select(r => r.Id);
        if (await db.SourceControlPublications.AnyAsync(p => p.OrganizationId == business && repositoryIds.Contains(p.RepositoryId) &&
            p.Status != SourceControlPublicationStatus.Merged && p.Status != SourceControlPublicationStatus.Superseded &&
            p.Status != SourceControlPublicationStatus.Failed && p.Status != SourceControlPublicationStatus.BranchPublishedExternalMerge, ct))
            dependencyBlockers.Add("Finish or supersede outstanding publications before disconnecting.");
        if (await db.DeliveryBuilds.AnyAsync(b => b.OrganizationId == business && repositoryIds.Contains(b.RepositoryId) &&
            b.Status != "Succeeded" && b.Status != "Failed" && b.Status != "Cancelled" && b.Status != "Exhausted", ct))
            dependencyBlockers.Add("Finish or cancel active delivery builds before disconnecting.");
        if (await db.RepositoryProvisioningRequests.AnyAsync(r => r.OrganizationId == business && r.ConnectionId == id &&
            (r.Status == RepositoryProvisioningStatus.Pending || r.Status == RepositoryProvisioningStatus.AwaitingApproval ||
             r.Status == RepositoryProvisioningStatus.Provisioning || r.Status == RepositoryProvisioningStatus.Quarantined), ct))
            dependencyBlockers.Add("Finish, cancel, or resolve outstanding repository provisioning first.");
        if (await db.SourceControlOnboardingSessions.AnyAsync(s => s.OrganizationId == business && s.ConnectionId == id &&
            (s.Status == SourceControlOnboardingStatus.InProgress || s.Status == SourceControlOnboardingStatus.AwaitingProvider), ct))
            dependencyBlockers.Add("Finish the active setup session first.");
        if (await db.SourceControlBusinessSettings.AnyAsync(s => s.OrganizationId == business && s.DefaultTemplateId != null &&
            db.SourceControlRepositoryTemplates.Any(t => t.Id == s.DefaultTemplateId && t.ConnectionId == id && t.OrganizationId == business), ct))
            dependencyBlockers.Add("Choose another default for new projects before disconnecting this connection.");
        return new(blockers.Count == 0 && dependencyBlockers.Count == 0, blockers.Concat(dependencyBlockers).Distinct().ToArray(),
            dependencyBlockers.Count == 0, dependencyBlockers);
    }

    public async Task<SourceControlConnectionDetails> DisconnectConnectionAsync(Guid business, Guid user, Guid id,
        DisconnectSourceControlConnectionRequest request, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, true, ct);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct) : null;
        var connection = await db.SourceControlConnections.AsTracking().SingleOrDefaultAsync(c => c.OrganizationId == business && c.Id == id, ct)
            ?? throw new KeyNotFoundException("Source-control connection not found.");
        if (connection.Name != request.ConfirmName) throw new ArgumentException("Confirm the connection's exact name before disconnecting.");
        if (connection.Revision != request.ExpectedRevision) throw new DbUpdateConcurrencyException("Connection changed; reload before disconnecting.");
        var plan = await DisconnectPlanAsync(business, connection, ct);
        if (!(request.SuspendDependentAccess ? plan.CanDisconnectWithDependencies : plan.CanDisconnect))
            throw new InvalidOperationException(string.Join(" ", request.SuspendDependentAccess ? plan.DependencyBlockers! : plan.Blockers));
        await RecordAsync("Started");
        var now = clock.GetUtcNow();
        connection.Status = SourceControlConnectionStatus.Disconnected; connection.DisconnectedAt = now;
        connection.UpdatedAt = now; connection.Revision++;
        if (request.SuspendDependentAccess)
        {
            foreach (var policy in await db.RepositoryProvisioningPolicies.AsTracking().Where(p => p.OrganizationId == business && p.ConnectionId == id && p.IsEnabled).ToListAsync(ct))
            { policy.IsEnabled = false; policy.Revision++; policy.UpdatedAt = now; }
            foreach (var template in await db.SourceControlRepositoryTemplates.AsTracking().Where(t => t.OrganizationId == business && t.ConnectionId == id && t.IsEnabled).ToListAsync(ct))
            { template.IsEnabled = false; template.Revision++; template.UpdatedAt = now; }
        }
        // Preserve installation identity and historical records. Reconnection must use authenticated onboarding.
        foreach (var credential in await db.SourceControlCredentials.AsTracking().Where(c => c.OrganizationId == business && c.ConnectionId == id && c.RevokedAt == null).ToListAsync(ct))
            credential.RevokedAt = now;
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        await RecordAsync("Completed");
        return await ConnectionAsync(business, user, id, ct);

        Task<Guid> RecordAsync(string outcome) => audit.AppendAsync(new("SourceControl.Connection.Disconnect", Category: "SourceControl", Outcome: outcome,
            OrganizationId: business, EntityType: "SourceControlConnection", EntityId: id,
            Actor: new AuditActor("User", ApplicationUserId: user)), ct);
    }
}
