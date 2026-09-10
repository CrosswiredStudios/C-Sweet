using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;
namespace CSweet.Infrastructure.Persistence;

internal static class WebPreviewConfigurations
{
    public static void Apply(ModelBuilder model)
    {
        model.Entity<WebHostRegistration>(entity =>
        {
            entity.ToTable("WebHostRegistrations"); entity.HasKey(x => x.Id);
            entity.Property(x => x.DisplayName).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.IdentityPublicKeyBase64).HasMaxLength(256).IsRequired();
            entity.Property(x => x.IdentityKeyDigest).HasMaxLength(71).IsRequired();
            entity.Property(x => x.RegistrationDigest).HasMaxLength(71).IsRequired();
            entity.Property(x => x.MaximumCapacityJson).HasColumnType("text").IsRequired();
            entity.Property(x => x.BootstrapJson).HasColumnType("text").IsRequired();
            entity.Property(x => x.ReportedHeartbeatJson).HasColumnType("text");
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.OrganizationId, x.RegistrationRequestId }).IsUnique();
            entity.HasIndex(x => x.IdentityKeyDigest).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.Status, x.LastHeartbeatAt });
            entity.HasOne<CSweet.Domain.Core.Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentInstallation>().WithMany().HasForeignKey(x => x.ProviderInstallationId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<WebPreviewGrantRecord>(entity =>
        {
            entity.ToTable("WebPreviewGrants"); entity.HasKey(x=>x.Id);
            entity.Property(x=>x.PolicyJson).HasColumnType("text").IsRequired();
            entity.Property(x=>x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x=>x.IdempotencyKey).HasMaxLength(200).IsRequired();
            entity.Property(x=>x.RequestDigest).HasMaxLength(71).IsRequired();
            entity.Property(x=>x.ApprovalContentDigest).HasMaxLength(71).IsRequired();
            entity.Property(x=>x.Revision).IsConcurrencyToken();
            entity.HasIndex(x=>new { x.OrganizationId,x.InstallationId,x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x=>new { x.OrganizationId,x.WorkstreamId,x.Status });
            entity.HasOne<CSweet.Domain.Core.Organization>().WithMany().HasForeignKey(x=>x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CSweet.Domain.Core.Workstream>().WithMany().HasForeignKey(x=>x.WorkstreamId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentInstallation>().WithMany().HasForeignKey(x=>x.InstallationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentInstallation>().WithMany().HasForeignKey(x=>x.ProviderInstallationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x=>x.ApprovalProposalId).IsUnique();
            entity.HasOne<CSweet.Domain.Core.ActionProposal>().WithMany().HasForeignKey(x=>x.ApprovalProposalId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CSweet.Domain.Core.Artifact>().WithMany().HasForeignKey(x=>x.ApprovalArtifactId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CSweet.Domain.Core.ArtifactRevision>().WithMany().HasForeignKey(x=>x.ApprovalRevisionId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<WebPreviewJobRecord>(entity =>
        {
            entity.ToTable("WebPreviewJobs"); entity.HasKey(x=>x.Id);
            entity.Property(x=>x.ManifestJson).HasColumnType("text").IsRequired();
            entity.Property(x=>x.ManifestDigest).HasMaxLength(71).IsRequired();
            entity.Property(x=>x.RequestDigest).HasMaxLength(71).IsRequired();
            entity.Property(x=>x.IdempotencyKey).HasMaxLength(200).IsRequired();
            entity.Property(x=>x.Phase).HasMaxLength(32).IsRequired();
            entity.Property(x=>x.FailureCode).HasMaxLength(128);
            entity.Property(x=>x.AccessReference).HasMaxLength(1024);
            entity.Property(x=>x.Revision).IsConcurrencyToken();
            entity.HasIndex(x=>new { x.OrganizationId,x.InstallationId,x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x=>new { x.OrganizationId,x.WorkstreamId,x.Phase,x.ExpiresAt });
            entity.HasOne<WebPreviewGrantRecord>().WithMany().HasForeignKey(x=>x.GrantId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
