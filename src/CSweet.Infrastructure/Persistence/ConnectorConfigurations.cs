using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CSweet.Infrastructure.Persistence;

public sealed class ConnectorExecutionConfiguration : IEntityTypeConfiguration<ConnectorExecution>
{
    public void Configure(EntityTypeBuilder<ConnectorExecution> entity)
    {
        entity.HasKey(x => x.Id);
        entity.HasIndex(x => new { x.OrganizationId, x.RequesterInstallationId, x.ConnectorInstallationId, x.Capability, x.IdempotencyKey }).IsUnique();
        entity.Property(x => x.IdempotencyKey).HasMaxLength(160);
        entity.Property(x => x.Capability).HasMaxLength(200);
        entity.Property(x => x.Revision).IsConcurrencyToken();
        entity.HasIndex(x => new { x.Status, x.UpdatedAt });
    }
}

public sealed class ConnectorProfileApprovalConfiguration : IEntityTypeConfiguration<ConnectorProfileApproval>
{
    public void Configure(EntityTypeBuilder<ConnectorProfileApproval> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.PackageDigest).HasMaxLength(256);
        entity.Property(x => x.ProfileId).HasMaxLength(200);
        entity.HasIndex(x => new { x.ConnectorInstallationId, x.PackageDigest, x.ProfileId }).IsUnique();
    }
}
