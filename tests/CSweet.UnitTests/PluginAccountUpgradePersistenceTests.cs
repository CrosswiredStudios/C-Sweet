using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CSweet.UnitTests;

public sealed class PluginAccountUpgradePersistenceTests
{
    [Fact]
    public void MigrationAndTerminalGuardsAreAdditiveAndRollbackRefusesToDiscardEvidence()
    {
        using var db=new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql("Host=localhost;Database=offline_script_only").Options);
        Assert.False(db.Database.HasPendingModelChanges());var versions=db.Database.GetMigrations().ToArray();
        var index=Array.FindIndex(versions,v=>v.EndsWith("_AddCompatiblePluginAccountUpgrades",StringComparison.Ordinal));Assert.True(index>0);
        var migrator=db.GetService<IMigrator>();var up=migrator.GenerateScript(versions[index-1],versions[index]);
        Assert.Contains("CREATE TABLE \"PluginAccountUpgrades\"",up);Assert.Contains("csweet_plugin_upgrade_guard",up);
        Assert.Contains("BEFORE INSERT OR UPDATE OR DELETE",up);Assert.Contains("BEFORE TRUNCATE",up);
        Assert.Contains("plugin_upgrade_authority_immutable",up);Assert.Contains("WHERE \"Status\" = 'Pending'",up);
        Assert.DoesNotContain("DROP TABLE",up);Assert.DoesNotContain("UPDATE \"PluginConnections\"",up);Assert.DoesNotContain("DELETE FROM",up);
        var down=migrator.GenerateScript(versions[index],versions[index-1]);Assert.Contains("IF EXISTS(SELECT 1 FROM \"PluginAccountUpgrades\")",down);
        Assert.True(down.IndexOf("Retain compatible upgrade intent",StringComparison.Ordinal)<down.IndexOf("DROP TABLE",StringComparison.Ordinal));
        var model=db.Model.FindEntityType(typeof(PluginAccountUpgrade))!;Assert.True(model.FindProperty("Revision")!.IsConcurrencyToken);
        Assert.Equal(2,model.GetIndexes().Count(x=>x.IsUnique && x.GetFilter()=="\"Status\" = 'Pending'"));
    }
    [Fact]
    public async Task ACompletedReceiptCannotBeInsertedWithoutThePendingIntentTransition()
    {
        await using var f=await PluginAccountUpgradeTests.F.Create();f.Db.PluginAccountUpgrades.Add(new(){Id=Guid.NewGuid(),Status="Completed",CompletedAt=DateTimeOffset.UtcNow});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Db.SaveChangesAsync());
    }
}
