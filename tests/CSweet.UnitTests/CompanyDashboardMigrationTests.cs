using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
namespace CSweet.UnitTests;

public sealed class CompanyDashboardMigrationTests
{
    [Fact]
    public void SnapshotMatchesDashboardModelAndMigrationGeneratesBothTables()
    {
        using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
        var initialized = db.GetService<IModelRuntimeInitializer>().Initialize(snapshot, designTime: true);
        var target = db.GetService<IDesignTimeModel>().Model;
        var differences = db.GetService<IMigrationsModelDiffer>().GetDifferences(initialized.GetRelationalModel(), target.GetRelationalModel());
        var dashboardDifferences = differences.Where(operation => operation.GetType().GetProperties()
            .Where(p => p.PropertyType == typeof(string)).Any(p =>
                (p.GetValue(operation) as string)?.Contains("CompanyDashboard", StringComparison.Ordinal) == true));
        Assert.Empty(dashboardDifferences);
        var sql = db.GetService<IMigrator>().GenerateScript("20260908210000_PersistPublicationReviewPatch", "20260908220000_AddCompanyDashboard");
        Assert.Contains("CREATE TABLE \"CompanyDashboardReports\"", sql);
        Assert.Contains("CREATE TABLE \"CompanyDashboardLayouts\"", sql);
        Assert.Contains("IX_CompanyDashboardReports_Latest", sql);
        Assert.Contains("FK_CompanyDashboardLayouts_User", sql);
    }
}
