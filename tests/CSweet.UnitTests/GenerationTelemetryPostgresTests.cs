using CSweet.Contracts.Realtime;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CSweet.UnitTests;

public sealed class GenerationTelemetryPostgresTests
{
    // Validate the production provider's DDL without opening a database connection.
    [Fact]
    public void MigrationMatchesTheModelAndOnlyAddsNullableLiveCounters()
    {
        using var db = new DesignTimeDbContextFactory().CreateDbContext([]);
        Assert.False(db.Database.HasPendingModelChanges());
        var migrations = db.Database.GetMigrations().ToArray();
        var index = Array.FindIndex(migrations, x => x.EndsWith("_AddLiveGenerationTelemetry", StringComparison.Ordinal));
        Assert.True(index > 0);
        var sql = db.GetService<IMigrator>().GenerateScript(migrations[index - 1], migrations[index]);
        foreach (var name in new[] { "GenerationStartedAt", "GenerationObservedAt", "GenerationOutputTokens", "GenerationIsEstimated" })
            Assert.Contains($"ADD \"{name}\"", sql);
        Assert.DoesNotContain("NOT NULL", sql);
        Assert.DoesNotContain("UPDATE \"AgentRunLogs\"", sql);
    }

    [GenerationPostgresFact]
    public async Task MigrationAndRollbackKeepTelemetryAndWakeHintsInTheSameTransaction()
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE"));
        Assert.Contains(settings.Host, new[] { "localhost", "127.0.0.1", "::1" });
        settings.Database = "generation_test_" + Guid.NewGuid().ToString("N"); settings.Pooling = false;
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(settings.ConnectionString).Options);
        try
        {
            await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            var run = new AgentRunLog { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), EmployeeId = Guid.NewGuid(),
                Status = "Running", StartedAt = DateTimeOffset.UtcNow };
            db.Add(run); await db.SaveChangesAsync();
            var auditCount = await db.AuditOutbox.CountAsync();
            var wakeCount = await db.ApplicationRealtimeOutbox.CountAsync();
            var id = run.Id;
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                RecordOutput(); await db.SaveChangesAsync();
                Assert.Equal(auditCount + 1, await db.AuditOutbox.CountAsync());
                Assert.Equal(wakeCount + 1, await db.ApplicationRealtimeOutbox.CountAsync());
                await transaction.RollbackAsync();
            }
            db.ChangeTracker.Clear();
            run = await db.AgentRunLogs.SingleAsync(x => x.Id == id);
            Assert.Null(run.GenerationObservedAt);
            Assert.Equal(auditCount, await db.AuditOutbox.CountAsync());
            Assert.Equal(wakeCount, await db.ApplicationRealtimeOutbox.CountAsync());
            // A new context models retry after a rolled-back producer transaction.
            await using var retry = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(settings.ConnectionString).Options);
            run = await retry.AgentRunLogs.SingleAsync(x => x.Id == id);
            RecordOutput(retry); await retry.SaveChangesAsync();
            db.ChangeTracker.Clear();
            Assert.Equal(80d, (await db.AgentRunLogs.SingleAsync(x => x.Id == id)).GenerationOutputTokens);
            Assert.Equal(auditCount + 1, await db.AuditOutbox.CountAsync());
            Assert.Equal(wakeCount + 1, await db.ApplicationRealtimeOutbox.CountAsync());
            Assert.Contains(await db.ApplicationRealtimeOutbox.ToListAsync(), x => x.EventType == AppRealtimeEvents.CurrentActivityChanged);

            void RecordOutput(CSweetDbContext? context = null)
            {
                run.GenerationStartedAt = DateTimeOffset.UtcNow.AddSeconds(-2);
                run.GenerationObservedAt = DateTimeOffset.UtcNow;
                run.GenerationOutputTokens = 80; run.GenerationIsEstimated = true;
                (context ?? db).QueueAudit(new("model.response.chunk", OrganizationId: run.OrganizationId,
                    EntityType: nameof(AgentRunLog), EntityId: run.Id, Employees: [new(run.EmployeeId!.Value)]));
            }
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    private sealed class GenerationPostgresFactAttribute : FactAttribute
    {
        public GenerationPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE")))
                Skip = "Set CSWEET_COMPUTE_TEST_DATABASE to a disposable loopback PostgreSQL server.";
        }
    }
}
