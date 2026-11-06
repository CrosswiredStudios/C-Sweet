using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CSweet.Infrastructure.Persistence;

/// <summary>
/// Mapping for <see cref="Delegation"/>. Registered with
/// <c>modelBuilder.ApplyConfiguration(new DelegationConfiguration());</c> in
/// <c>CSweetDbContext.OnModelCreating</c> (see the integration note in the workstream report).
/// </summary>
public sealed class DelegationConfiguration : IEntityTypeConfiguration<Delegation>
{
    public void Configure(EntityTypeBuilder<Delegation> entity)
    {
        entity.ToTable("Delegations");
        entity.HasKey(x => x.Id);

        entity.Property(x => x.BusinessId).HasMaxLength(200).IsRequired();
        entity.Property(x => x.Title).HasMaxLength(200).IsRequired();
        entity.Property(x => x.Scope).HasMaxLength(4000).IsRequired();
        entity.Property(x => x.ReturnContract).HasMaxLength(4000);
        entity.Property(x => x.InputArtifactIdsJson).HasColumnType("text").IsRequired();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(x => x.CreatedAt).IsRequired();

        // Accept/decline/complete are decided by whoever gets there first: the status the caller
        // read becomes the concurrency token, so a second decision fails instead of silently
        // overwriting the first.
        entity.Property(x => x.Status).IsConcurrencyToken();

        // The JSON string is the only stored representation; the parsed list is a view.
        entity.Ignore(x => x.InputArtifactIds);
        entity.Ignore(x => x.IsSettled);

        entity.HasIndex(x => x.BusinessId);
        entity.HasIndex(x => x.Status);
        entity.HasIndex(x => new { x.BusinessId, x.Status, x.UpdatedAt });
        entity.HasIndex(x => new { x.ToInstallationId, x.Status });

        // Both employees are required, so removal cascades: an installation that no longer exists
        // cannot honour the packet, and the set-based business purge (OrganizationDataPurgeService)
        // discovers scoped tables through OrganizationId/AgentInstallation.BusinessId only. A
        // restrictive reference here would be a table the purge cannot order and therefore a
        // permanent block on business deletion and on employee removal.
        entity.HasOne<AgentInstallation>()
            .WithMany()
            .HasForeignKey(x => x.FromInstallationId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<AgentInstallation>()
            .WithMany()
            .HasForeignKey(x => x.ToInstallationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
