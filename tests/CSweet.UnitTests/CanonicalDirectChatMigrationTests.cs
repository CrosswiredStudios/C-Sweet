using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CSweet.UnitTests;

public sealed class CanonicalDirectChatMigrationTests
{
    [Fact]
    public void ModelAndMigrationAgreeAndPreserveHistoricalReferences()
    {
        using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseNpgsql("Host=localhost;Database=offline_script_only").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var sql = db.GetService<IMigrator>().GenerateScript("20260908004653_AddMediaAssetChunkIntegrity",
            "20260908013000_CanonicalDirectChats");
        Assert.Contains("CREATE UNIQUE INDEX", sql);
        Assert.Contains("MergedIntoConversationId", sql);
        Assert.DoesNotContain("DELETE FROM", sql);
        Assert.DoesNotContain("DROP TABLE", sql);
    }
}
