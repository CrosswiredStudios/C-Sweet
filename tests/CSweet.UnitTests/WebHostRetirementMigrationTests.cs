using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CSweet.UnitTests;

public sealed class WebHostRetirementMigrationTests
{
    private const string MigrationId = "20260912012616_RetireWebHostProofOfConcept";

    [Fact]
    public void Retirement_drops_only_PoC_tables_and_preserves_shared_execution_schema()
    {
        using var db = Database();
        Assert.False(db.Database.HasPendingModelChanges());
        var migrations = db.GetService<IMigrationsAssembly>();
        var migration = migrations.CreateMigration(migrations.Migrations[MigrationId], db.Database.ProviderName!);
        string[] expected = ["WebHostCommands", "WebHostRegistrations", "WebPreviewBrowserSessions",
            "WebPreviewEvidence", "WebPreviewFindings", "WebPreviewGrants", "WebPreviewJobs",
            "WebPreviewProjectAdmissions", "WebPreviewTriageRoutes"];
        Assert.Equal(expected.Order(), migration.UpOperations.OfType<DropTableOperation>().Select(x => x.Name).Order());
        var tables = db.Model.GetEntityTypes().Select(x => x.GetTableName()).ToHashSet();
        Assert.DoesNotContain(tables, name => expected.Contains(name));
        foreach (var table in new[] { "PreviewSessions", "DeliveryBuilds", "ExecutionWorkloadAssignments", "ActionProposals", "AgentPlatformEventOutbox" })
            Assert.Contains(table, tables);
    }

    [Fact]
    public void Production_script_checks_physical_teardown_before_discarding_state()
    {
        using var db = Database();
        var sql = db.GetService<IMigrator>().GenerateScript("20260911032817_AddPrivatePreviewDispatch", MigrationId);
        var guard = sql.IndexOf("RAISE EXCEPTION", StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < sql.IndexOf("DROP TABLE", StringComparison.Ordinal));
        Assert.Contains("\"TeardownConfirmedAt\" IS NULL", sql);
        Assert.Contains("WHERE \"ActionType\" = 'web-preview.grant' AND \"Status\" = 'Pending'", sql);
        Assert.Contains("WHERE \"EventType\" = 'com.csweet.web-preview.changed.v1' AND \"Status\" = 'Pending'", sql);
        Assert.DoesNotContain("DELETE FROM", sql);
        Assert.DoesNotContain("TRUNCATE", sql);
    }

    private static CSweetDbContext Database() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseNpgsql("Host=localhost;Database=offline_script_only").Options);
}
