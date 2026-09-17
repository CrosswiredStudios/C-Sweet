using CSweet.Domain.Analytics;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    public DbSet<BenchmarkDefinition> BenchmarkDefinitions => Set<BenchmarkDefinition>();
    public DbSet<BenchmarkCampaign> BenchmarkCampaigns => Set<BenchmarkCampaign>();
    public DbSet<BenchmarkTrial> BenchmarkTrials => Set<BenchmarkTrial>();
    public DbSet<BenchmarkAssessment> BenchmarkAssessments => Set<BenchmarkAssessment>();
    public DbSet<BenchmarkWake> BenchmarkWakes => Set<BenchmarkWake>();

    private void ConfigureBenchmarks(ModelBuilder model)
    {
        model.Entity<BenchmarkDefinition>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(256);
            e.Property(x => x.Digest).HasMaxLength(64);
            e.HasIndex(x => new { x.FamilyId, x.Version }).IsUnique();
        });
        model.Entity<BenchmarkCampaign>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.IdempotencyKey).HasMaxLength(160);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.Scheduling).HasMaxLength(16);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => new { x.CreatedBy, x.IdempotencyKey }).IsUnique();
            e.HasOne<BenchmarkDefinition>().WithMany().HasForeignKey(x => x.DefinitionId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<BenchmarkTrial>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.EvaluationStatus).HasMaxLength(32);
            e.Property(x => x.Detail).HasMaxLength(2048);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => new { x.CampaignId, x.VariantIndex, x.Repetition }).IsUnique();
            e.HasIndex(x => x.OrganizationId).IsUnique();
            e.HasIndex(x => new { x.Status, x.NextRecoveryAt });
            e.HasOne<BenchmarkCampaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<BenchmarkAssessment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(24);
            e.Property(x => x.CriterionKey).HasMaxLength(160);
            e.Property(x => x.IdempotencyKey).HasMaxLength(160);
            e.Property(x => x.Score).HasPrecision(6, 3);
            e.HasIndex(x => new { x.TrialId, x.IdempotencyKey }).IsUnique();
            e.HasOne<BenchmarkTrial>().WithMany().HasForeignKey(x => x.TrialId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<BenchmarkWake>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ProcessedAt, x.CreatedAt });
        });
    }

    private void CaptureBenchmarkWakes()
    {
        if (ChangeTracker.Entries<BenchmarkDefinition>().Any(x => x.State is EntityState.Modified or EntityState.Deleted) ||
            ChangeTracker.Entries<BenchmarkAssessment>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Benchmark definitions and assessments are immutable; append a new version.");

        foreach (var entry in ChangeTracker.Entries<Workstream>().Where(x => x.State == EntityState.Modified &&
                     x.Property(p => p.Status).IsModified).ToArray())
        {
            var id = WorkLifecycleEvents.Local.LastOrDefault(x => x.ResourceKind == "Project" &&
                x.ResourceId == entry.Entity.Id)?.Id ?? Guid.NewGuid();
            if (!BenchmarkWakes.Local.Any(x => x.Id == id))
                BenchmarkWakes.Add(new() { Id = id, OrganizationId = entry.Entity.OrganizationId, CreatedAt = DateTimeOffset.UtcNow });
        }
        foreach (var entry in ChangeTracker.Entries<BenchmarkTrial>().Where(x => x.State == EntityState.Added ||
                     x.State == EntityState.Modified && (x.Property(p => p.Status).IsModified ||
                     x.Property(p => p.EvaluationStatus).IsModified)).ToArray())
        {
            if (!ChangeTracker.Entries<BenchmarkWake>().Any(x => x.State == EntityState.Added && x.Entity.TrialId == entry.Entity.Id))
                BenchmarkWakes.Add(new() { Id = Guid.NewGuid(), TrialId = entry.Entity.Id, CreatedAt = DateTimeOffset.UtcNow });
        }
    }
}
