using CSweet.Domain.PersonalAssistant;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    public DbSet<PersonalAssistantProfile> PersonalAssistantProfiles => Set<PersonalAssistantProfile>();
    public DbSet<PersonalAssistantMessage> PersonalAssistantMessages => Set<PersonalAssistantMessage>();
    public DbSet<RepositoryOnboardingDraft> RepositoryOnboardingDrafts => Set<RepositoryOnboardingDraft>();
    public DbSet<RepositoryOnboardingSource> RepositoryOnboardingSources => Set<RepositoryOnboardingSource>();

    private static void ConfigurePersonalAssistant(ModelBuilder model)
    {
        model.Entity<PersonalAssistantProfile>(e =>
        {
            e.HasKey(x => x.ApplicationUserId);
            e.Property(x => x.Name).HasMaxLength(100).HasDefaultValue("AI Assistant");
            e.HasIndex(x => x.AgentInstallationId).IsUnique();
            e.HasOne<AgentInstallation>().WithMany().HasForeignKey(x => x.AgentInstallationId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<PersonalAssistantMessage>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ApplicationUserId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.ApplicationUserId, x.DraftId, x.CreatedAt });
            e.Property(x => x.Role).HasMaxLength(20);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.IdempotencyKey).HasMaxLength(160);
            e.Property(x => x.Content).HasMaxLength(32000);
        });
        model.Entity<RepositoryOnboardingDraft>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ApplicationUserId, x.UpdatedAt });
            e.HasIndex(x => new { x.Status, x.LeaseUntil });
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.Error).HasMaxLength(2048);
            e.Property(x => x.GitHubStateHash).HasMaxLength(64);
            e.Property(x => x.LeaseOwner).HasMaxLength(160);
        });
        model.Entity<RepositoryOnboardingSource>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Draft).WithMany(x => x.Sources).HasForeignKey(x => x.DraftId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.DraftId, x.Kind, x.InstallationId, x.ExternalRepositoryId });
            e.HasIndex(x => x.RepositoryId).IsUnique();
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.Name).HasMaxLength(256);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.Owner).HasMaxLength(160);
            e.Property(x => x.Snapshot).HasMaxLength(64);
            e.Property(x => x.ArchiveKey).HasMaxLength(64);
            e.Property(x => x.Error).HasMaxLength(2048);
        });
        model.Entity<SystemConfiguration>().Property(x => x.PersonalAssistantModel).HasMaxLength(256);
        model.Entity<AgentInstallation>().HasIndex(x => x.OwnerApplicationUserId);
    }
}
