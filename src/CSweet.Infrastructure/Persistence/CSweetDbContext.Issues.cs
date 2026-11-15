using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    public DbSet<DurableMutationReceipt> DurableMutationReceipts => Set<DurableMutationReceipt>();
    public DbSet<WorkIssueCase> WorkIssueCases => Set<WorkIssueCase>();
    public DbSet<WorkIssueCommission> WorkIssueCommissions => Set<WorkIssueCommission>();

    private static void ConfigureIssues(ModelBuilder model)
    {
        model.Entity<WorkIssueCase>(e => {
            e.HasKey(x => x.Id);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.Property(x => x.ObligationKey).HasMaxLength(160);
            e.HasIndex(x => new { x.OrganizationId, x.BoardId, x.ObligationKey, x.Episode }).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.Status, x.UpdatedAt });
            foreach (var name in new[] { "Statement", "Detail", "BriefTitle", "BriefSummary", "NextAction", "SourceJson", "ScopeKeysJson", "LinksJson", "ResolutionEvidence" })
                e.Property(name).HasColumnType("text");
        });
        model.Entity<WorkIssueCommission>(e => {
            e.HasKey(x => x.Id); e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            foreach (var name in new[] { "IssueIdsJson", "SourceJson", "Question", "Failure", "SupersededSessionIdsJson" }) e.Property(name).HasColumnType("text");
        });
        model.Entity<DurableMutationReceipt>(e => {
            e.HasKey(x => x.Id);
            e.Property(x => x.ResourceKey).HasMaxLength(160);
            e.Property(x => x.MutationKey).HasMaxLength(160);
            e.Property(x => x.ContentDigest).HasMaxLength(64);
            e.Property(x => x.ResultJson).HasColumnType("text");
            e.HasIndex(x => new { x.OrganizationId, x.PrincipalId, x.ResourceKey, x.MutationKey }).IsUnique();
        });
    }
}
