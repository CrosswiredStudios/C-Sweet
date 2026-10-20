using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    public DbSet<ResourceCleanupRequest> ResourceCleanupRequests => Set<ResourceCleanupRequest>();

    private static void ConfigureResourceCleanup(ModelBuilder model)
    {
        model.Entity<ResourceCleanupRequest>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasMaxLength(64);
            entity.Property(x => x.LastError).HasMaxLength(1024);
            entity.HasIndex(x => new { x.Kind, x.ResourceId }).IsUnique();
            entity.HasIndex(x => new { x.CompletedAt, x.AvailableAt });
        });
    }
}
