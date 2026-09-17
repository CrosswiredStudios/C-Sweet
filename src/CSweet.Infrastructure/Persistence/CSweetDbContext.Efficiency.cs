using CSweet.Domain.Analytics;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    public DbSet<WorkLifecycleEvent> WorkLifecycleEvents => Set<WorkLifecycleEvent>();

    public DbSet<WorkExecutionContext> WorkExecutionContexts => Set<WorkExecutionContext>();
    public DbSet<WorkExecutionInterval> WorkExecutionIntervals => Set<WorkExecutionInterval>();

    private void ConfigureEfficiency(ModelBuilder model)
    {
        model.Entity<WorkExecutionContext>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => new { x.OrganizationId, x.RootWorkItemId });
            e.HasIndex(x => x.AgentWorkItemId);
        });
        model.Entity<WorkExecutionInterval>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.AgentWorkAttemptId);
            e.HasIndex(x => new { x.OrganizationId, x.StartedAt });
            e.Property(x => x.ConfirmedThrough).IsConcurrencyToken();
        });
        model.Entity<WorkLifecycleEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ResourceKind).HasMaxLength(24);
            e.Property(x => x.Status).HasMaxLength(80);
            e.Property(x => x.PreviousStatus).HasMaxLength(80);
            e.HasIndex(x => new { x.OrganizationId, x.ResourceId, x.OccurredAt });
        });
        model.Entity<Domain.Setup.AgentRunLog>(e =>
        {
            e.Property(x => x.MeasurementKind).HasMaxLength(24).HasDefaultValue("Legacy");
            e.Property(x => x.AttributionKind).HasMaxLength(32).HasDefaultValue("Unknown");
            e.Property(x => x.AncestorWorkItemIdsJson).HasDefaultValue("[]");
            e.Property(x => x.AgentPackageVersion).HasMaxLength(160);
            e.Property(x => x.ConfigurationDigest).HasMaxLength(512);
            e.HasIndex(x => new { x.OrganizationId, x.WorkItemId, x.StartedAt });
            e.HasIndex(x => new { x.OrganizationId, x.WorkstreamId, x.StartedAt });
            e.HasIndex(x => new { x.BenchmarkTrialId, x.StartedAt });
        });
    }

    private void CaptureWorkLifecycle()
    {
        if (ChangeTracker.Entries<WorkLifecycleEvent>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Work lifecycle history is append-only.");
        foreach (var entry in ChangeTracker.Entries<WorkTask>().Where(x =>
                     x.State == EntityState.Added || x.State == EntityState.Modified &&
                     x.Property(t => t.Status).IsModified).ToArray())
        {
            var before = entry.State == EntityState.Added ? null : entry.Property(x => x.Status).OriginalValue.ToString();
            if (before == entry.Entity.Status.ToString()) continue;
            AddLifecycle(entry.Entity.OrganizationId, entry.Entity.Id, "WorkItem", before,
                entry.Entity.Status.ToString(), entry.Entity.Revision,
                entry.State == EntityState.Added ? entry.Entity.CreatedAt : entry.Entity.UpdatedAt);
        }
        foreach (var entry in ChangeTracker.Entries<Workstream>().Where(x =>
                     x.State == EntityState.Added || x.State == EntityState.Modified &&
                     x.Property(t => t.Status).IsModified).ToArray())
        {
            var before = entry.State == EntityState.Added ? null : entry.Property(x => x.Status).OriginalValue.ToString();
            if (before == entry.Entity.Status.ToString()) continue;
            AddLifecycle(entry.Entity.OrganizationId, entry.Entity.Id, "Project", before,
                entry.Entity.Status.ToString(), entry.Entity.Revision,
                entry.State == EntityState.Added ? entry.Entity.CreatedAt : entry.Entity.UpdatedAt);
        }
    }

    public void AddLifecycle(Guid organization, Guid resource, string kind, string? before, string status,
        long revision, DateTimeOffset occurredAt)
    {
        // Retry the pending receipt, but preserve repeated transitions even when callers reuse timestamps.
        if (ChangeTracker.Entries<WorkLifecycleEvent>().Any(x => x.State == EntityState.Added &&
            x.Entity.ResourceId == resource && x.Entity.ResourceKind == kind && x.Entity.SourceRevision == revision &&
            x.Entity.PreviousStatus == before && x.Entity.Status == status)) return;
        var id = Guid.NewGuid();
        WorkLifecycleEvents.Add(new()
        {
            Id = id, OrganizationId = organization, ResourceId = resource, ResourceKind = kind,
            PreviousStatus = before, Status = status, SourceRevision = revision,
            OccurredAt = occurredAt == default ? DateTimeOffset.UtcNow : occurredAt
        });
    }
}
