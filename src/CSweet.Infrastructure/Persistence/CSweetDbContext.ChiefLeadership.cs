using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

public partial class CSweetDbContext
{
    public DbSet<ChiefLeadershipAgenda> ChiefLeadershipAgendas => Set<ChiefLeadershipAgenda>();
    public DbSet<ChiefFocusRecord> ChiefFocusRecords => Set<ChiefFocusRecord>();
    public DbSet<ChiefCoverageRecord> ChiefCoverageRecords => Set<ChiefCoverageRecord>();
    public DbSet<ChiefFocusPrompt> ChiefFocusPrompts => Set<ChiefFocusPrompt>();
    public DbSet<ChiefLeadershipReceipt> ChiefLeadershipReceipts => Set<ChiefLeadershipReceipt>();

    private static void ConfigureChiefLeadership(ModelBuilder model)
    {
        model.Entity<ChiefLeadershipAgenda>(e =>
        {
            e.HasKey(x => x.Id); e.Property(x => x.ProfileKey).HasMaxLength(80);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.Id).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Revision).IsConcurrencyToken();
        });
        model.Entity<ChiefFocusRecord>(e =>
        {
            e.HasKey(x => x.Id); e.Property(x => x.DomainId).HasMaxLength(80); e.Property(x => x.CustomText).HasMaxLength(4000);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.OrganizationId, x.Revision }).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.SourceDecisionId }).IsUnique().HasFilter("\"SourceDecisionId\" IS NOT NULL");
        });
        model.Entity<ChiefCoverageRecord>(e =>
        {
            e.HasKey(x => x.Id); e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.ProfileKey).HasMaxLength(80); e.Property(x => x.DomainId).HasMaxLength(80);
            e.Property(x => x.Label).HasMaxLength(160); e.Property(x => x.Description).HasMaxLength(2048);
            e.Property(x => x.CoverageStatus).HasMaxLength(40); e.Property(x => x.ExecutionStatus).HasMaxLength(40);
            e.Property(x => x.Rationale).HasMaxLength(2048); e.Property(x => x.CoverageModel).HasMaxLength(80);
            e.Property(x => x.NextAction).HasMaxLength(40); e.Property(x => x.BlockCode).HasMaxLength(160);
            e.Property(x => x.InputFingerprint).HasMaxLength(64); e.Property(x => x.AcceptedResultJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.OrganizationId, x.ProfileKey, x.ProfileVersion, x.DomainId }).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.ExecutionStatus, x.Id });
        });
        model.Entity<ChiefFocusPrompt>(e =>
        {
            e.HasKey(x => x.Id); e.Property(x => x.ProfileKey).HasMaxLength(80); e.Property(x => x.Purpose).HasMaxLength(40);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.IdempotencyKey).HasMaxLength(160);
            e.HasIndex(x => new { x.InstallationId, x.IdempotencyKey }).IsUnique();
        });
        model.Entity<ChiefLeadershipReceipt>(e =>
        {
            e.HasKey(x => x.Id); e.Property(x => x.OperationKey).HasMaxLength(160); e.Property(x => x.RequestDigest).HasMaxLength(64);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.ResultJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.OrganizationId, x.InstallationId, x.OperationKey }).IsUnique();
        });
    }
}
