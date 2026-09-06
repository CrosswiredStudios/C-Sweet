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
        return new(blockers.Count == 0, blockers);
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
        if (!plan.CanDisconnect) throw new InvalidOperationException(string.Join(" ", plan.Blockers));
        await RecordAsync("Started");
        var now = clock.GetUtcNow();
        connection.Status = SourceControlConnectionStatus.Disconnected; connection.DisconnectedAt = now;
        connection.UpdatedAt = now; connection.Revision++;
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
