using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;
namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    private async Task CaptureWorkAuthorityWakesAsync(CancellationToken ct)
    {
        var changed = ChangeTracker.Entries<ScopedActionGrant>().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Where(e => CSweet.Infrastructure.WorkManagement.BoardPlanningRecoveryPolicy.IsPlanningAction(e.Entity.Action)).ToArray();
        foreach (var group in changed.GroupBy(e => e.Entity.OrganizationId))
        {
            var principals = group.Select(e => e.Entity.SubjectId).Distinct().ToArray();
            var owners = await CoreOrganizationUsers.Where(u => u.OrganizationId == group.Key && u.IsActive && u.ArchivedAt == null &&
                (principals.Contains(u.Id) || u.AgentInstallationId != null && principals.Contains(u.AgentInstallationId.Value)))
                .ToArrayAsync(ct);
            foreach (var owner in owners)
            {
                var manager = await CoreOrganizationUsers.SingleOrDefaultAsync(u => u.OrganizationId == group.Key && u.Id == owner.ReportsToOrganizationUserId &&
                    u.IsActive && u.ArchivedAt == null && u.AgentInstallationId != null, ct);
                if (manager is null) continue;
                var basis = MutationContent.Digest(group.Where(e => e.Entity.SubjectId == owner.Id || e.Entity.SubjectId == owner.AgentInstallationId)
                    .OrderBy(e => e.Entity.Id).Select(e => new { e.Entity.Id, e.Entity.Revision, e.Entity.Action, e.Entity.ScopeKind, e.Entity.ScopeId,
                        e.Entity.RevokedAt, e.Entity.ExpiresAt, deleted = e.State == EntityState.Deleted }).ToArray());
                var key = "work-authority:" + owner.Id.ToString("N") + ":" + manager.AgentInstallationId!.Value.ToString("N") + ":" + basis;
                if (AgentPlatformEventOutbox.Local.Any(e => e.IdempotencyKey == key) || await AgentPlatformEventOutbox.AnyAsync(e => e.OrganizationId == group.Key && e.IdempotencyKey == key, ct)) continue;
                var now = DateTimeOffset.UtcNow;
                AgentPlatformEventOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = group.Key, TargetInstallationId = manager.AgentInstallationId,
                    EventType = W.WorkRecoveryEvents.Changed, DataJson = JsonSerializer.Serialize(new W.WorkRecoveryChangedEvent(owner.Id), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    IdempotencyKey = key, OccurredAt = now, NextAttemptAt = now, Status = AgentPlatformEventOutboxStatus.Pending });
            }
        }
    }
}
