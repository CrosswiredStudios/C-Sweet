using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

internal static class SourceControlModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlatformGitHubAppCredential>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.OwnerLogin).HasMaxLength(100).IsRequired();
            entity.Property(x => x.AppName).HasMaxLength(100).IsRequired();
            entity.Property(x => x.AppSlug).HasMaxLength(100).IsRequired();
            entity.Property(x => x.InstallUrl).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.ProtectedPrivateKey).HasMaxLength(65536).IsRequired();
            entity.Property(x => x.ProtectionVersion).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.FailureMessage).HasMaxLength(2048);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.Kind, x.Status, x.UpdatedAt });
            entity.HasIndex(x => x.Kind)
                .IsUnique()
                .HasFilter("\"Status\" = 'Active'");
        });

        modelBuilder.Entity<PlatformSourceControlSetupSession>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.CurrentStep).HasMaxLength(80).IsRequired();
            entity.Property(x => x.GitHubOrganization).HasMaxLength(100).IsRequired();
            entity.Property(x => x.PublicBaseUrl).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.ManifestCallbackUrl).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.PendingAppKind).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.StateNonceHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.LastError).HasMaxLength(2048);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.StartedByApplicationUserId, x.Status, x.UpdatedAt });
            entity.HasIndex(x => new { x.StateNonceHash, x.StateExpiresAt });
        });

        modelBuilder.Entity<SourceControlConnection>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Provider).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.Mode).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.ProviderAccountId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.AccountLogin).HasMaxLength(256).IsRequired();
            entity.Property(x => x.AccountType).HasMaxLength(32).IsRequired();
            entity.Property(x => x.AllowedHost).HasMaxLength(255);
            entity.Property(x => x.SshHostFingerprintsJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.LastHealthError).HasMaxLength(2048);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.Provider, x.ProviderAccountId }).IsUnique();
            entity.HasIndex(x => new { x.Provider, x.ProviderAccountId })
                .IsUnique()
                .HasFilter("\"Provider\" = 'GitHub'");
        });

        modelBuilder.Entity<SourceControlCredential>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.ProtectedPayload).HasMaxLength(65536).IsRequired();
            entity.Property(x => x.ProtectionVersion).HasMaxLength(80).IsRequired();
            entity.HasIndex(x => new { x.OrganizationId, x.ConnectionId, x.RevokedAt });
            entity.HasOne(x => x.Connection)
                .WithMany(x => x.Credentials)
                .HasForeignKey(x => new { x.OrganizationId, x.ConnectionId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SourceControlRepository>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            entity.Property(x => x.ExternalRepositoryId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Owner).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(256).IsRequired();
            entity.Property(x => x.CanonicalPath).HasMaxLength(512).IsRequired();
            entity.Property(x => x.CloneUrl).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.DefaultBranch).HasMaxLength(255).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.LastHealthError).HasMaxLength(2048);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.OrganizationId, x.ConnectionId, x.ExternalRepositoryId }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.CanonicalPath }).IsUnique();
            entity.HasOne(x => x.Connection)
                .WithMany(x => x.Repositories)
                .HasForeignKey(x => new { x.OrganizationId, x.ConnectionId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RepositoryProvisioningPolicy>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            entity.Property(x => x.NamePrefix).HasMaxLength(100).IsRequired();
            entity.Property(x => x.NamingPattern).HasMaxLength(512).IsRequired();
            entity.Property(x => x.ApprovedTemplatesJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.OrganizationId, x.ConnectionId }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.DefaultTeamId });
            entity.HasOne(x => x.Connection)
                .WithMany(x => x.ProvisioningPolicies)
                .HasForeignKey(x => new { x.OrganizationId, x.ConnectionId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SourceControlRepositoryTemplate>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            entity.Property(x => x.ExternalRepositoryId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Owner).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(256).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(160).IsRequired();
            entity.Property(x => x.DefaultBranch).HasMaxLength(255).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.OrganizationId, x.ConnectionId, x.ExternalRepositoryId }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.ConnectionId, x.Owner, x.Name }).IsUnique();
            entity.HasOne(x => x.Connection)
                .WithMany(x => x.RepositoryTemplates)
                .HasForeignKey(x => new { x.OrganizationId, x.ConnectionId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RepositoryProvisioningRequest>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            entity.Property(x => x.ProjectDisplayName).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(2000).IsRequired();
            entity.Property(x => x.RepositoryName).HasMaxLength(100).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.FailureCode).HasMaxLength(80);
            entity.Property(x => x.FailureMessage).HasMaxLength(2048);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.OrganizationId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.Status, x.CreatedAt });
            entity.HasOne(x => x.Connection)
                .WithMany()
                .HasForeignKey(x => new { x.OrganizationId, x.ConnectionId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Policy)
                .WithMany()
                .HasForeignKey(x => new { x.OrganizationId, x.PolicyId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Template)
                .WithMany()
                .HasForeignKey(x => new { x.OrganizationId, x.TemplateId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Repository)
                .WithMany()
                .HasForeignKey(x => new { x.OrganizationId, x.RepositoryId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SourceControlApproval>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(48).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.DecisionComment).HasMaxLength(2048);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.OrganizationId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.Status, x.CreatedAt });
            entity.HasIndex(x => new { x.OrganizationId, x.ProvisioningRequestId })
                .IsUnique()
                .HasFilter("\"ProvisioningRequestId\" IS NOT NULL");
            entity.HasIndex(x => new { x.OrganizationId, x.MergeJobId })
                .IsUnique()
                .HasFilter("\"MergeJobId\" IS NOT NULL");
            entity.HasOne<RepositoryProvisioningRequest>()
                .WithMany()
                .HasForeignKey(x => new { x.OrganizationId, x.ProvisioningRequestId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TeamRepositoryPolicy>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MergeApprovalMode).HasConversion<string>().HasMaxLength(48).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.OrganizationId, x.TeamId, x.RepositoryId }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.TeamId, x.IsPrimary })
                .IsUnique()
                .HasFilter("\"IsPrimary\" = TRUE AND \"DisabledAt\" IS NULL");
            entity.HasOne(x => x.Repository)
                .WithMany(x => x.TeamPolicies)
                .HasForeignKey(x => new { x.OrganizationId, x.RepositoryId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SourceControlOnboardingSession>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SelectedMode).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.CurrentStep).HasMaxLength(80).IsRequired();
            entity.Property(x => x.StateNonceHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.DraftJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.OrganizationId, x.StartedByOrganizationUserId, x.Status });
            entity.HasIndex(x => new { x.StateNonceHash, x.ExpiresAt }).IsUnique();
        });

        modelBuilder.Entity<SourceControlWorkspace>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            entity.Property(x => x.WorkspaceKey).HasMaxLength(256).IsRequired();
            entity.Property(x => x.BaseCommitSha).HasMaxLength(64).IsRequired();
            entity.Property(x => x.BranchName).HasMaxLength(255).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.LastError).HasMaxLength(2048);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new
            {
                x.OrganizationId,
                x.AgentInstallationId,
                x.WorkItemId,
                x.AssignmentRevision
            }).IsUnique();
            entity.HasIndex(x => x.WorkspaceKey).IsUnique();
            entity.HasOne(x => x.Repository)
                .WithMany()
                .HasForeignKey(x => new { x.OrganizationId, x.RepositoryId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SourceControlPublication>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            entity.Property(x => x.CommitSha).HasMaxLength(64).IsRequired();
            entity.Property(x => x.TargetBranch).HasMaxLength(255).IsRequired();
            entity.Property(x => x.TicketBranch).HasMaxLength(255).IsRequired();
            entity.Property(x => x.PullRequestId).HasMaxLength(128);
            entity.Property(x => x.PullRequestUrl).HasMaxLength(2048);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(48).IsRequired();
            entity.Property(x => x.ChangedFilesJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.ValidationResultsJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.OrganizationId, x.WorkspaceId, x.CommitSha }).IsUnique();
            entity.HasOne(x => x.Workspace)
                .WithMany()
                .HasForeignKey(x => new { x.OrganizationId, x.WorkspaceId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Repository)
                .WithMany()
                .HasForeignKey(x => new { x.OrganizationId, x.RepositoryId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SourceControlValidation>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CommitSha).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.ResultsJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.FailureMessage).HasMaxLength(2048);
            entity.HasIndex(x => new
            {
                x.OrganizationId,
                x.PublicationId,
                x.ValidatorAgentInstallationId,
                x.CommitSha
            }).IsUnique();
            entity.HasOne(x => x.Publication)
                .WithMany()
                .HasForeignKey(x => new { x.OrganizationId, x.PublicationId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SourceControlMergeAuthorization>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            entity.Property(x => x.CommitSha).HasMaxLength(64).IsRequired();
            entity.Property(x => x.DecisionSignature).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.RevocationReason).HasMaxLength(1024);
            entity.HasIndex(x => new
            {
                x.OrganizationId,
                x.PublicationId,
                x.AuthorizedByOrganizationUserId,
                x.CommitSha
            }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.ExpiresAt, x.RevokedAt });
            entity.HasOne(x => x.Publication)
                .WithMany()
                .HasForeignKey(x => new { x.OrganizationId, x.PublicationId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SourceControlMergeJob>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ExpectedHeadSha).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ApprovalMode).HasConversion<string>().HasMaxLength(48).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.MergeCommitSha).HasMaxLength(64);
            entity.Property(x => x.FailureCode).HasMaxLength(80);
            entity.Property(x => x.FailureMessage).HasMaxLength(2048);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.OrganizationId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.PublicationId, x.ExpectedHeadSha }).IsUnique();
            entity.HasOne(x => x.Publication)
                .WithMany()
                .HasForeignKey(x => new { x.OrganizationId, x.PublicationId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.LeadAuthorization)
                .WithMany()
                .HasForeignKey(x => new { x.OrganizationId, x.LeadAuthorizationId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
