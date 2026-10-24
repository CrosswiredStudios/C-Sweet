using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    private static void ConfigurePluginAccounts(ModelBuilder model)
    {
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
