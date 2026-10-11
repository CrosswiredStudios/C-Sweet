using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CSweet.UnitTests;

public sealed class GitHubAppPersonalOwnershipMigrationTests
{
    [Fact]
    public void MigrationPreservesLegacyOrganizationOwnersAndMatchesTheModel()
    {
        using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
        var assembly = db.GetService<IMigrationsAssembly>();
        var snapshot = db.GetService<IModelRuntimeInitializer>().Initialize(assembly.ModelSnapshot!.Model, designTime: true);
        var model = db.GetService<IDesignTimeModel>().Model;
        Assert.Empty(db.GetService<IMigrationsModelDiffer>().GetDifferences(snapshot.GetRelationalModel(), model.GetRelationalModel()));
        var migration = assembly.Migrations.Keys.Single(key => key.EndsWith("_GitHubAppPersonalOwnership", StringComparison.Ordinal));
        var previous = assembly.Migrations.Keys.OrderBy(key => key).TakeWhile(key => key != migration).Last();
        var sql = db.GetService<IMigrator>().GenerateScript(previous, migration);
        Assert.Contains("ADD \"OwnerType\" character varying(20) NOT NULL DEFAULT 'Organization'", sql);
        Assert.DoesNotContain("DELETE FROM", sql);
    }
}
