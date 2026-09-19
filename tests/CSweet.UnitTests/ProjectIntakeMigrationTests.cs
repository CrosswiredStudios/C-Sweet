using System.Text;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
namespace CSweet.UnitTests;

public sealed class ProjectIntakeMigrationTests
{
    [Fact]
    public void Project_model_matches_snapshot_and_rollout_sql_preserves_execution_boundaries()
    {
        using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
        var migrations = db.GetService<IMigrationsAssembly>();
        var snapshot = db.GetService<IModelRuntimeInitializer>().Initialize(migrations.ModelSnapshot!.Model, designTime: true);
        var target = db.GetService<IDesignTimeModel>().Model;
        Assert.Empty(db.GetService<IMigrationsModelDiffer>().GetDifferences(snapshot.GetRelationalModel(), target.GetRelationalModel()));
        var before = migrations.Migrations.Keys.Last(x => string.CompareOrdinal(x, "20260918195338_RequireProjectIntake") < 0);
        var sql = db.GetService<IMigrator>().GenerateScript(before, "20260918205850_RetainQueuedProjectWork");
        Assert.Contains("CREATE TABLE \"ProjectIntakes\"", sql);
        Assert.Contains("CREATE TABLE \"ProjectParticipants\"", sql);
        Assert.Contains("WITH RECURSIVE started", sql);
        Assert.Contains("a.\"StartedAt\" IS NOT NULL", sql);
        Assert.Contains("PendingWorkItemId", sql);
        Assert.Contains(SoftwarePrototypeProfile.Digest, sql);
    }
    [Fact]
    public void Built_in_profile_is_valid_without_an_agent_package()
    {
        var validated = WorkstreamProfileDefinitionValidator.Validate(new CSweet.Contracts.Plugins.PluginWorkstreamProfileContribution
            { Key = SoftwarePrototypeProfile.Key, Version = 1 }, Encoding.UTF8.GetBytes(SoftwarePrototypeProfile.Definition));
        Assert.Equal(SoftwarePrototypeProfile.Digest, validated.Digest);
        Assert.Equal("general-work.v1", validated.DefaultBoardProfileKey);
    }
}
