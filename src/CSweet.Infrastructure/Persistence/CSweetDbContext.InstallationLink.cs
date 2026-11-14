using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    public DbSet<InstallationLink> InstallationLinks => Set<InstallationLink>();
    public DbSet<DirectoryPublication> DirectoryPublications => Set<DirectoryPublication>();

    private static void ConfigureInstallationLink(ModelBuilder model)
    {
        model.Entity<InstallationLink>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Issuer).HasMaxLength(200).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(80).IsRequired();
            e.Property(x => x.ProtectedInstallationKey).IsRequired();
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => x.InstallationId).IsUnique();
        });

        model.Entity<DirectoryPublication>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Subject).HasMaxLength(450).IsRequired();
            e.Property(x => x.Role).HasMaxLength(32).IsRequired();
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.LastError).HasMaxLength(64);
            e.HasIndex(x => x.Subject).IsUnique();
            e.HasIndex(x => x.NextAttemptAt);
        });
    }
}
