using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    private static void ConfigurePluginAccounts(ModelBuilder model)
    {
        model.Entity<PluginAccountCleanupReceipt>(e=>
        {
            e.HasKey(x=>x.CopyId);e.Property(x=>x.RequestHash).HasMaxLength(64);e.Property(x=>x.KeyId).HasMaxLength(80);
            e.Property(x=>x.Payload).HasMaxLength(16384);e.Property(x=>x.Signature).HasMaxLength(2048);
        });
        model.Entity<PluginAccountAdministration>(e => { e.HasKey(x=>x.PluginInstallationId); e.Property(x=>x.Revision).IsConcurrencyToken(); });
        model.Entity<PluginAccountDeletion>(e =>
        {
            e.HasKey(x=>x.AccountGroupId); e.Property(x=>x.Status).HasMaxLength(40); e.Property(x=>x.Revision).IsConcurrencyToken();
            e.HasIndex(x=>new { x.PluginInstallationId,x.Status });
        });
        model.Entity<PluginAccountDataCopy>(e =>
        {
            e.HasKey(x=>x.Id); e.Property(x=>x.Store).HasMaxLength(160); e.Property(x=>x.Revision).IsConcurrencyToken();
            e.Property(x=>x.CleanupNextAttemptAt).IsConcurrencyToken();
            e.HasIndex(x=>new { x.AccountGroupId,x.Store,x.RecordId }).IsUnique();
            e.HasIndex(x=>new { x.ConsumerInstallationId,x.DeletedAt });
        });
        model.Entity<ConnectorProfileApproval>().Property(x => x.ProviderProfileHash).HasMaxLength(64);
        model.Entity<PluginAccountRevocationJob>(e =>
        {
            e.HasKey(x => x.Id); e.Property(x => x.Revision).IsConcurrencyToken();
            e.Property(x => x.ProfileId).HasMaxLength(200); e.Property(x => x.ProfileHash).HasMaxLength(64);
            e.Property(x => x.CredentialKey).HasMaxLength(256); e.Property(x => x.Status).HasMaxLength(40);
            e.HasIndex(x => new { x.Status, x.NextAttempt });
            e.HasIndex(x => x.ConnectionId).IsUnique();
            e.HasOne<AgentInstallation>().WithMany().HasForeignKey(x => x.PluginInstallationId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<PluginAccountSetupSession>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.Property(x => x.PackageDigest).HasMaxLength(128);
            e.Property(x => x.ProfileHash).HasMaxLength(64);
            e.Property(x => x.PolicyVersion).HasMaxLength(1000);
            e.Property(x => x.StateHash).HasMaxLength(64);
            e.Property(x => x.RedirectUri).HasMaxLength(2048);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.FailureCode).HasMaxLength(64);
            e.Property(x => x.PageToken).HasMaxLength(2048);
            e.Property(x => x.SeenPageTokensJson).HasColumnType("text");
            e.Property(x => x.VerifiedSubject).HasMaxLength(512);
            e.Property(x => x.VerifiedName).HasMaxLength(512);
            e.Property(x => x.IdentityAuthority).HasMaxLength(2048);
            e.Property(x => x.RetiredCredentialKey).HasMaxLength(256);
            e.Property(x => x.ScopeSetIdsJson).HasColumnType("text");
            e.Property(x => x.RequestedScopesJson).HasColumnType("text");
            e.Property(x => x.ObservedResourcesJson).HasColumnType("text");
            e.HasIndex(x => new { x.PluginInstallationId, x.ApplicationUserId, x.Status });
            e.HasIndex(x => new { x.Status, x.LeaseUntil });
            e.HasOne<AgentInstallation>().WithMany().HasForeignKey(x => x.PluginInstallationId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<PluginAccountOperator>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x=>x.Revision).IsConcurrencyToken();
            e.HasIndex(x=>new { x.PluginInstallationId,x.ApplicationUserId,x.AccountGroupId }).IsUnique().AreNullsDistinct(false);
            e.HasIndex(x => new { x.PluginInstallationId, x.ApplicationUserId });
            e.HasOne<AgentInstallation>().WithMany().HasForeignKey(x => x.PluginInstallationId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<PluginConnection>(e =>
        {
            e.Property(x=>x.NextInventoryCheckAt).IsConcurrencyToken();
            e.Property(x => x.ActiveCredentialAttemptId).IsConcurrencyToken();
            e.Property(x => x.RefreshLeaseOwner).IsConcurrencyToken();
            e.Property(x => x.CredentialProfileHash).HasMaxLength(64);
        });
        model.Entity<PluginAccountGroup>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.DisplayName).HasMaxLength(512).IsRequired();
            e.Property(x => x.ProviderProfile).HasMaxLength(200).IsRequired();
            e.Property(x => x.VerifiedSubject).HasMaxLength(512);
            e.Property(x => x.IdentityAuthority).HasMaxLength(2048);
            e.Property(x => x.IdentityKind).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => new { x.PluginInstallationId, x.ProviderProfile });
            e.HasIndex(x => new { x.PluginInstallationId, x.ProviderProfile, x.IdentityAuthority, x.VerifiedSubject }).IsUnique()
                .HasFilter("\"VerifiedSubject\" IS NOT NULL AND \"State\" = 'Connected'");
            e.HasOne<AgentInstallation>().WithMany().HasForeignKey(x => x.PluginInstallationId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<PluginAccountResource>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ResourceType).HasMaxLength(200).IsRequired();
            e.Property(x => x.ExternalResourceId).HasMaxLength(512).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(512).IsRequired();
            e.Property(x => x.Handle).HasMaxLength(512);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => new { x.AccountGroupId, x.ResourceType, x.ExternalResourceId }).IsUnique();
            e.HasOne<PluginAccountGroup>().WithMany().HasForeignKey(x => x.AccountGroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AgentInstallation>().WithMany().HasForeignKey(x => x.PluginInstallationId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<PluginAccountResourceAuthorization>(e =>
        {
            e.HasKey(x => new { x.ResourceId, x.ConnectionId });
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasOne<PluginAccountResource>().WithMany().HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<PluginConnection>().WithMany().HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<PluginAccountBusinessGrant>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Scope).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.SelectedResourceIdsJson).HasColumnType("text").IsRequired();
            e.Property(x => x.CapabilitiesJson).HasColumnType("text").IsRequired();
            e.Property(x => x.PackageDigest).HasMaxLength(128).IsRequired();
            e.Property(x => x.PolicyVersion).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => new { x.PluginInstallationId, x.AccountGroupId, x.OrganizationId }).IsUnique();
            e.HasOne<PluginAccountGroup>().WithMany().HasForeignKey(x => x.AccountGroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AgentInstallation>().WithMany().HasForeignKey(x => x.PluginInstallationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<AgentCapabilityBinding>(e =>
        {
            e.HasOne<PluginAccountBusinessGrant>().WithMany().HasForeignKey(x => x.PluginAccountBusinessGrantId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PluginAccountResource>().WithMany().HasForeignKey(x => x.PluginAccountResourceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PluginConnection>().WithMany().HasForeignKey(x => x.PluginAccountConnectionId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
