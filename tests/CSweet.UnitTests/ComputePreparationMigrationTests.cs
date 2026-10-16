using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CSweet.UnitTests;

public sealed class ComputePreparationMigrationTests
{
    [Fact]
    public void AttemptIdentityMigrationMatchesModelAndKeepsLegacySetups()
    {
        using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseNpgsql("Host=localhost;Database=offline_script_only").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations["20261016190000_AddComputePreparationAttempt"], db.Database.ProviderName!);
        var sql = string.Join("\n", db.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, migration.TargetModel).Select(command => command.CommandText));
        Assert.Contains("ALTER TABLE \"ComputeLocalSetups\" ADD \"PreparationAttemptId\" uuid", sql);
        Assert.DoesNotContain("NOT NULL", sql);
        Assert.DoesNotContain("DROP", sql);
    }
}
