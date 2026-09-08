using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;
namespace CSweet.Infrastructure.Persistence;

internal static class CompanyDashboardConfigurations
{
    public static void Apply(ModelBuilder model)
    {
        model.Entity<CompanyDashboardReport>(e =>
        {
            e.ToTable("CompanyDashboardReports");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(20).IsRequired();
            e.Property(x => x.ReporterName).HasMaxLength(300).IsRequired();
            e.Property(x => x.PayloadJson).IsRequired();
            e.HasIndex(x => new { x.OrganizationId, x.Kind, x.WorkstreamId, x.PublishedAt }).HasDatabaseName("IX_CompanyDashboardReports_Latest");
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<CompanyDashboardLayout>(e =>
        {
            e.ToTable("CompanyDashboardLayouts");
            e.HasKey(x => new { x.OrganizationId, x.OrganizationUserId });
            e.Property(x => x.OrderJson).IsRequired();
            e.HasOne<OrganizationUser>().WithMany().HasForeignKey(x => x.OrganizationUserId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("FK_CompanyDashboardLayouts_User");
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
