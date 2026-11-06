using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    private void CaptureGovernanceInvalidations()
    {
        ChangeTracker.DetectChanges();
        var changes = new Dictionary<Guid, (long? Posture, long? Grant, bool Inactive)>();
        foreach (var entry in ChangeTracker.Entries<AgentInstallation>().Where(x => x.State == EntityState.Modified).ToArray())
        {
            var row = entry.Entity;
            if (entry.Property(x => x.GovernanceRevision).IsModified ||
                entry.Property(x => x.IsEnabled).IsModified || entry.Property(x => x.RevisionStatus).IsModified)
                changes[row.Id] = (row.GovernanceRevision, null, !row.IsEnabled || row.RevisionStatus != PluginRevisionStatus.Active);
        }
        foreach (var entry in ChangeTracker.Entries<AgentInstallationGrant>().Where(x => x.State == EntityState.Modified && x.Property(p => p.GrantRevision).IsModified).ToArray())
        {
            changes.TryGetValue(entry.Entity.AgentInstallationId, out var previous);
            changes[entry.Entity.AgentInstallationId] = (previous.Posture, entry.Entity.GrantRevision, previous.Inactive);
        }
        foreach (var (installation, change) in changes)
        {
            var rows = AgentCapabilityApprovals.Where(x => x.AgentInstallationId == installation &&
                (x.Status == "Pending" || x.Status == "Approved") &&
                (change.Inactive || change.Posture != null && x.PostureRevision != change.Posture || change.Grant != null && x.GrantRevision != change.Grant)).ToList();
            foreach (var row in rows)
            {
                row.Status = "Superseded"; row.Revision++; row.UpdatedAt = DateTimeOffset.UtcNow;
                QueueAudit(new AuditEventWriteRequest("agents.approval.superseded", OrganizationId: row.OrganizationId == Guid.Empty ? null : row.OrganizationId,
                    EntityType: nameof(AgentCapabilityApproval), EntityId: row.Id,
                    Summary: "An operation approval was superseded by a change to agent authority.", Actor: new AuditActor("Platform")));
                ApplicationRealtimeOutbox.Add(new()
                {
                    Id = Guid.NewGuid(), OrganizationId = row.OrganizationId == Guid.Empty ? null : row.OrganizationId,
                    RecipientApplicationUserId = row.OrganizationId == Guid.Empty
                        ? AgentInstallations.Where(x => x.Id == installation).Select(x => x.OwnerApplicationUserId).SingleOrDefault() : null,
                    EventType = CSweet.Infrastructure.Agents.GovernanceEvents.Changed, Subject = row.Id.ToString("D"),
                    DataJson = JsonSerializer.Serialize(new { installationId = installation, id = row.Id, state = row.Status }),
                    OccurredAt = row.UpdatedAt, NextAttemptAt = row.UpdatedAt
                });
            }
        }
    }
}
