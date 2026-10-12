using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    public DbSet<TrackedDelegation> TrackedDelegations => Set<TrackedDelegation>();
    public DbSet<WorkTrackingSubscription> WorkTrackingSubscriptions => Set<WorkTrackingSubscription>();
    public DbSet<WorkTrackingUpdate> WorkTrackingUpdates => Set<WorkTrackingUpdate>();
    public DbSet<WorkTrackingOperationReceipt> WorkTrackingOperationReceipts => Set<WorkTrackingOperationReceipt>();

    private static void ConfigureDelegation(ModelBuilder model)
    {
        model.Entity<WorkTrackingOperationReceipt>(e =>
        {
            e.HasIndex(x => new { x.OrganizationId, x.ActorId, x.Operation, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.ActorId, x.UpdateId });
            e.Property(x => x.IdempotencyKey).HasMaxLength(160);
            e.Property(x => x.Operation).HasMaxLength(40);
            e.Property(x => x.RequestHash).HasMaxLength(64);
        });
        model.Entity<TrackedDelegation>(e =>
        {
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => new { x.OrganizationId, x.AuthoredByOrganizationUserId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.RecipientId, x.Status });
            e.HasIndex(x => new { x.OrganizationId, x.WorkItemId });
            e.Property(x => x.IdempotencyKey).HasMaxLength(160);
            e.Property(x => x.SourceChecksum).HasMaxLength(64);
            e.Property(x => x.RequestHash).HasMaxLength(64);
            e.Property(x => x.Status).HasMaxLength(40);
        });
        model.Entity<WorkTrackingSubscription>(e =>
        {
            e.HasIndex(x => new { x.OrganizationId, x.WorkItemId, x.FollowerId, x.DelegationId }).IsUnique().AreNullsDistinct(false);
            e.HasIndex(x => new { x.OrganizationId, x.FollowerId, x.UnfollowedAt });
        });
        model.Entity<WorkTrackingUpdate>(e =>
        {
            e.HasIndex(x => new { x.OrganizationId, x.AuthorId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.DelegationId, x.ReportedAt, x.AvailableAt });
            e.HasIndex(x => new { x.OrganizationId, x.WorkItemId, x.OccurredAt, x.Id });
            e.Property(x => x.IdempotencyKey).HasMaxLength(160);
            e.Property(x => x.RequestHash).HasMaxLength(64);
            e.Property(x => x.Kind).HasMaxLength(40);
        });
    }

    // Record changes at the mutation boundary, not by polling agents or copying private task content.
    private void CaptureTrackedWorkChanges()
    {
        foreach (var entry in ChangeTracker.Entries<WorkTask>().Where(x => x.State == EntityState.Modified &&
                     x.Property(t => t.Status).IsModified && x.Property(t => t.Status).OriginalValue != x.Entity.Status).ToArray())
        {
            var item = entry.Entity;
            var key = $"work-status:{item.Id:N}:{item.Revision}:{item.Status}";
            if (WorkTrackingUpdates.Local.Any(x => x.IdempotencyKey == key)) continue;
            var terminal = item.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled;
            var blocked = item.Status == WorkTaskStatus.Blocked;
            var now = ExecutionClock.GetUtcNow();
            WorkTrackingUpdates.Add(new()
            {
                Id = Guid.NewGuid(), OrganizationId = item.OrganizationId, WorkItemId = item.Id,
                SourceRevision = item.Revision, Kind = terminal ? "WorkTerminal" : blocked ? "Blocked" : "Progress",
                Summary = $"Work is {item.Status.ToString().ToLowerInvariant()}.", IdempotencyKey = key,
                OccurredAt = now, AvailableAt = terminal || blocked ? now : now.AddMinutes(5)
            });
        }
    }
}
