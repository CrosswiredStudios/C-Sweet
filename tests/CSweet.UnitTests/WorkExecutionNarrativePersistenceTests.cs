using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CSweet.UnitTests;

public sealed class WorkExecutionNarrativePersistenceTests
{
    [Fact]
    public void AcceptedAndBlockedOutcomesHaveNoDatabaseNarrativeCeiling()
    {
        using var db = new DesignTimeDbContextFactory().CreateDbContext([]);
        Assert.False(db.Database.HasPendingModelChanges());
        var properties = new (Type Entity, string Property)[]
        {
            (typeof(WorkStageExecution), nameof(WorkStageExecution.LastSummary)),
            (typeof(WorkStageExecution), nameof(WorkStageExecution.LastError)),
            (typeof(WorkItemExecution), nameof(WorkItemExecution.BlockedReason)),
            (typeof(WorkDeliveryExecution), nameof(WorkDeliveryExecution.BlockedReason)),
            (typeof(WorkExecutionAttempt), nameof(WorkExecutionAttempt.ErrorMessage)),
            (typeof(WorkTask), nameof(WorkTask.BlockReason))
        };
        foreach (var (entity, name) in properties)
        {
            var property = db.Model.FindEntityType(entity)!.FindProperty(name)!;
            Assert.Null(property.GetMaxLength());
            Assert.Equal("text", property.GetColumnType());
        }

        var migrations = db.Database.GetMigrations().ToArray();
        var index = Array.FindIndex(migrations, x => x.EndsWith("_UnlimitWorkExecutionNarratives", StringComparison.Ordinal));
        Assert.True(index > 0);
        var sql = db.GetService<IMigrator>().GenerateScript(migrations[index - 1], migrations[index]);
        Assert.Equal(6, sql.Split(" TYPE text", StringSplitOptions.None).Length - 1);
        Assert.Contains("ALTER TABLE \"WorkStageExecutions\" ALTER COLUMN \"LastSummary\" TYPE text", sql);
        Assert.DoesNotContain("UPDATE ", sql);
        Assert.DoesNotContain("DROP COLUMN", sql);
        Assert.DoesNotContain("substring", sql, StringComparison.OrdinalIgnoreCase);
    }
}
