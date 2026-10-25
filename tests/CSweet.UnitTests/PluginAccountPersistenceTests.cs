using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CSweet.UnitTests;

public sealed class PluginAccountPersistenceTests
{
    [Fact]
    public void MigrationIsAdditiveAndKeepsLegacyConnectionsUniqueWithoutOpeningADatabase()
    {
        using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseNpgsql("Host=localhost;Database=offline_script_only").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var migrations = db.Database.GetMigrations().ToArray();
        var index = Array.FindIndex(migrations, x => x.EndsWith("_AddEnterprisePluginAccounts", StringComparison.Ordinal));
        Assert.True(index > 0);
        var sql = db.GetService<IMigrator>().GenerateScript(migrations[index - 1], migrations[index]);
        Assert.Contains("CREATE TABLE \"PluginAccountGroups\"", sql);
        Assert.Contains("CREATE TABLE \"PluginAccountResources\"", sql);
        Assert.Contains("CREATE TABLE \"PluginAccountResourceAuthorizations\"", sql);
        Assert.Contains("CREATE TABLE \"PluginAccountBusinessGrants\"", sql);
        Assert.Contains("WHERE \"AccountGroupId\" IS NULL", sql);
        Assert.DoesNotContain("DROP TABLE", sql);
        Assert.DoesNotContain("DELETE FROM", sql);
        Assert.DoesNotContain("CoreWorkTasks", sql);
        var accountIndex = Array.FindIndex(migrations,x => x.EndsWith("_AddPerAccountOAuthSetup",StringComparison.Ordinal));
        Assert.True(accountIndex > index);
        var accountSql = db.GetService<IMigrator>().GenerateScript(migrations[accountIndex-1],migrations[accountIndex]);
        Assert.Contains("CREATE TABLE \"PluginAccountSetupSessions\"",accountSql);
        Assert.Contains("CREATE TABLE \"PluginAccountRevocationJobs\"",accountSql);
        Assert.Contains("\"VerifiedSubject\" IS NOT NULL",accountSql);
        Assert.DoesNotContain("DROP TABLE",accountSql);
        var lifecycleIndex=Array.FindIndex(migrations,x=>x.EndsWith("_AddPluginAccountLifecycleEnforcement",StringComparison.Ordinal));
        Assert.True(lifecycleIndex>accountIndex);
        var lifecycleSql=db.GetService<IMigrator>().GenerateScript(migrations[lifecycleIndex-1],migrations[lifecycleIndex]);
        Assert.Contains("CREATE TABLE \"PluginAccountDeletions\"",lifecycleSql);
        Assert.Contains("CREATE TABLE \"PluginAccountDataCopies\"",lifecycleSql);
        Assert.Contains("NULLS NOT DISTINCT",lifecycleSql);
        Assert.DoesNotContain("DROP TABLE",lifecycleSql);
        var cleanupIndex=Array.FindIndex(migrations,x=>x.EndsWith("_AddPluginCleanupConfirmations",StringComparison.Ordinal));
        Assert.True(cleanupIndex>lifecycleIndex);
        var cleanupSql=db.GetService<IMigrator>().GenerateScript(migrations[cleanupIndex-1],migrations[cleanupIndex]);
        Assert.Contains("CREATE TABLE \"PluginAccountCleanupReceipts\"",cleanupSql);
        Assert.Contains("plugin_cleanup_receipt_immutable",cleanupSql);Assert.DoesNotContain("DROP TABLE",cleanupSql);
        foreach (var type in new[] { typeof(PluginAccountGroup), typeof(PluginAccountResource), typeof(PluginAccountBusinessGrant) })
            Assert.True(db.Model.FindEntityType(type)!.FindProperty("Revision")!.IsConcurrencyToken);
        Assert.True(db.Model.FindEntityType(typeof(PluginConnection))!.FindProperty("AuthorizationRevision")!.IsConcurrencyToken);
    }
}
