using CSweet.Application.Agents;
using CSweet.Contracts.Agents;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Agents;

public sealed class DatabaseAgentPostureStore(CSweetDbContext db) : IAgentPostureStore
{
    public async ValueTask<AgentPostureAssignment?> FindAsync(Guid installationId, CancellationToken cancellationToken = default)
    {
        var row = await db.AgentInstallations.AsNoTracking().Where(x => x.Id == installationId)
            .Select(x => new { x.Posture, x.PostureChangedAt, x.PostureChangedBy }).SingleOrDefaultAsync(cancellationToken);
        if (row is null || row.Posture is null) return null;
        if (!AgentAuthority.TryParse(row.Posture, out var posture))
            throw new InvalidOperationException("The stored agent posture is invalid; execution is refused.");
        return new(installationId, posture, row.PostureChangedAt, row.PostureChangedBy);
    }

    public async ValueTask<bool> IsActiveAsync(Guid installationId, CancellationToken cancellationToken = default) =>
        await db.AgentInstallations.AsNoTracking().AnyAsync(x => x.Id == installationId &&
            x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active, cancellationToken);

    public async ValueTask SaveAsync(AgentPostureAssignment assignment, CancellationToken cancellationToken = default)
    {
        // Serialize against an in-flight approval claim so a downgrade cannot commit underneath one.
        await using var transaction = await GovernanceTransaction.OpenAsync(db, assignment.InstallationId.ToString("D"), cancellationToken);
        var row = await db.AgentInstallations.SingleOrDefaultAsync(x => x.Id == assignment.InstallationId, cancellationToken)
            ?? throw new KeyNotFoundException("Installation not found.");
        var previous = row.Posture ?? "Manual";
        row.Posture = assignment.Posture.ToString();
        row.PostureChangedAt = assignment.ChangedAt;
        row.PostureChangedBy = assignment.ChangedByApplicationUserId;
        row.GovernanceRevision++;
        Guid.TryParse(row.BusinessId, out var organization);
        db.QueueAudit(new CSweet.Application.Setup.AuditEventWriteRequest("agents.posture.changed",
            OrganizationId: organization == Guid.Empty ? null : organization, EntityType: "AgentInstallation", EntityId: row.Id,
            Summary: $"Agent posture changed from {previous} to {row.Posture}.",
            Actor: new CSweet.Application.Setup.AuditActor("Human", ApplicationUserId: assignment.ChangedByApplicationUserId)));
        await GovernanceEvents.EnqueueAsync(db, organization, row.Id, row.Id, $"{row.Posture}:{row.GovernanceRevision}", true, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask<bool> RemoveAsync(Guid installationId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await GovernanceTransaction.OpenAsync(db, installationId.ToString("D"), cancellationToken);
        var row = await db.AgentInstallations.SingleOrDefaultAsync(x => x.Id == installationId, cancellationToken);
        if (row is null || row.Posture is null) return false;
        var previous = row.Posture;
        row.Posture = null; row.PostureChangedAt = null; row.PostureChangedBy = null; row.GovernanceRevision++;
        Guid.TryParse(row.BusinessId, out var organization);
        // A reset is a posture change; the ledger must say who did it and what it replaced.
        db.QueueAudit(new CSweet.Application.Setup.AuditEventWriteRequest("agents.posture.reset",
            OrganizationId: organization == Guid.Empty ? null : organization, EntityType: "AgentInstallation", EntityId: row.Id,
            Summary: $"Agent posture reset from {previous} to {AgentPostureService.Default}.",
            Actor: new CSweet.Application.Setup.AuditActor("Human")));
        await GovernanceEvents.EnqueueAsync(db, organization, row.Id, row.Id, $"Manual:{row.GovernanceRevision}", true, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
