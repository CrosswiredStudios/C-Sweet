using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

internal static class ComputeConfigurations
{
    internal static void Apply(ModelBuilder model)
    {
        model.Entity<ComputeLocalSetup>(entity =>
        {
            entity.ToTable("ComputeLocalSetups"); entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.OrganizationId).IsUnique();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.Property(x => x.State).HasMaxLength(32);
            entity.Property(x => x.HandoffHash).HasMaxLength(64);
            entity.Property(x => x.TemplateId).HasMaxLength(64);
            entity.Property(x => x.ErrorCode).HasMaxLength(64);
            entity.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ComputeAgentAccess>(entity =>
        {
            entity.ToTable("ComputeAgentAccess"); entity.HasKey(x => x.Id);
            entity.HasOne<AgentInstallation>().WithMany().HasForeignKey(x => x.Id).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ComputeLocalSetup>().WithMany().HasForeignKey(x => x.SetupId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Workstream>().WithMany().HasForeignKey(x => x.WorkstreamId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ComputeAuditOutbox>(entity =>
        {
            entity.ToTable("ComputeAuditOutbox"); entity.HasKey(x => x.Id);
            entity.Property(x => x.RequestJson).HasColumnType("text").IsRequired();
            entity.HasIndex(x => new { x.DeliveredAt, x.CreatedAt, x.Id });
        });
        model.Entity<ComputeNodeRegistration>(entity =>
        {
            entity.ToTable("ComputeNodes"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.ProviderId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.KeyId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.VerificationPublicKeyBase64).HasMaxLength(1024).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasAlternateKey(x => new { x.Id, x.OrganizationId });
            entity.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ComputeTemplateRegistration>(entity =>
        {
            entity.ToTable("ComputeTemplates"); entity.HasKey(x => x.Id);
            entity.Property(x => x.TemplateId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.TemplateJson).HasColumnType("text").IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasAlternateKey(x => new { x.Id, x.OrganizationId });
            entity.HasIndex(x => new { x.OrganizationId, x.TemplateId }).IsUnique();
            entity.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ComputeTemplatePlacement>(entity =>
        {
            entity.ToTable("ComputeTemplatePlacements"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TemplateRegistrationId, x.NodeId }).IsUnique();
            entity.HasOne<ComputeNodeRegistration>().WithMany().HasForeignKey(x => new { x.NodeId, x.OrganizationId })
                .HasPrincipalKey(x => new { x.Id, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ComputeTemplateRegistration>().WithMany().HasForeignKey(x => new { x.TemplateRegistrationId, x.OrganizationId })
                .HasPrincipalKey(x => new { x.Id, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ComputeEnvironment>(entity =>
        {
            entity.ToTable("ComputeEnvironments"); entity.HasKey(x => x.Id);
            entity.Property(x => x.DesiredEnvironmentKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.RequestDigest).HasMaxLength(71).IsRequired();
            entity.Property(x => x.SpecificationJson).HasColumnType("text").IsRequired();
            entity.Property(x => x.ProviderId).HasMaxLength(128);
            entity.Property(x => x.ProviderResourceId).HasMaxLength(256);
            entity.Property(x => x.LastFailureCode).HasMaxLength(128);
            entity.Property(x => x.DesiredState).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Persistence).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.Ignore(x => x.HoldsReservation);
            entity.HasIndex(x => new { x.OrganizationId, x.InstallationId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.InstallationId, x.DesiredEnvironmentKey }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.InstallationId, x.WorkstreamId, x.Id });
            entity.HasIndex(x => new { x.DesiredState, x.LeaseExpiresAt });
            entity.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentInstallation>().WithMany().HasForeignKey(x => x.InstallationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Workstream>().WithMany().HasForeignKey(x => x.WorkstreamId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ComputeProviderWake>(entity =>
        {
            entity.ToTable("ComputeProviderWakes"); entity.HasKey(x => x.Id);
            entity.Property(x => x.ProviderId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => x.OperationId).IsUnique();
            entity.HasIndex(x => new { x.PublishedAt, x.NextAttemptAt });
            entity.HasIndex(x => new { x.OrganizationId, x.NodeId, x.ProviderId, x.CreatedAt });
            entity.HasOne<ComputeOperation>().WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ComputeOperation>(entity =>
        {
            entity.ToTable("ComputeOperations"); entity.HasKey(x => x.Id);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160);
            entity.Property(x => x.RequestDigest).HasMaxLength(71);
            entity.HasIndex(x => new { x.OrganizationId, x.InstallationId, x.IdempotencyKey }).IsUnique();
            entity.Property(x => x.Action).HasMaxLength(128).IsRequired();
            entity.Property(x => x.AuthorityJson).HasColumnType("text").IsRequired();
            entity.Property(x => x.LastResultDigest).HasMaxLength(71);
            entity.Property(x => x.TemplateJson).HasColumnType("text").IsRequired();
            entity.Property(x => x.WorkloadJson).HasColumnType("text");
            entity.Property(x => x.ResultJson).HasColumnType("text");
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.FailureCode).HasMaxLength(128);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.Status, x.NextAttemptAt });
            entity.HasIndex(x => new { x.EnvironmentId, x.Generation });
            entity.HasOne<ComputeEnvironment>().WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<CSweet.Domain.Security.ScopedActionGrant>().Property(x => x.ConstraintsJson).HasColumnType("text").IsRequired();
        model.Entity<ComputeRequestReceipt>(entity =>
        {
            entity.ToTable("ComputeRequestReceipts"); entity.HasKey(x => x.Id);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.RequestDigest).HasMaxLength(71).IsRequired();
            entity.HasIndex(x => new { x.OrganizationId, x.InstallationId, x.IdempotencyKey }).IsUnique();
            entity.HasOne<ComputeEnvironment>().WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ComputeAdmission>(entity =>
        {
            entity.ToTable("ComputeAdmissions"); entity.HasKey(x => x.InstallationId);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasOne<AgentInstallation>().WithMany().HasForeignKey(x => x.InstallationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
