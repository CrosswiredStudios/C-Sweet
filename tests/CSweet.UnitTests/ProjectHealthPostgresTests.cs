using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CSweet.UnitTests;

public sealed class ProjectHealthPostgresTests
{
    [HealthPostgresFact]
    public async Task IncidentAndOutboxRollbackTogetherAndOpenEpisodeIsUnique()
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE"));
        Assert.Contains(settings.Host, new[] { "localhost", "127.0.0.1", "::1" });
        settings.Database = "project_health_test_" + Guid.NewGuid().ToString("N"); settings.Pooling = false;
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(settings.ConnectionString).Options;
        await using var db = new CSweetDbContext(options);
        try
        {
            await db.Database.MigrateAsync();
            var org = Guid.NewGuid(); var project = Guid.NewGuid(); var actor = Guid.NewGuid();
            ProjectIncident NewIncident() => new() { Id = Guid.NewGuid(), OrganizationId = org, WorkstreamId = project,
                ProducerEmployeeId = actor, CurrentRecipientId = actor, Fingerprint = "idle", DetectedAt = DateTimeOffset.UtcNow,
                LastProgressAt = DateTimeOffset.UtcNow, EscalateAt = DateTimeOffset.UtcNow.AddMinutes(15) };
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                db.ProjectIncidents.Add(NewIncident());
                db.AgentPlatformEventOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, EventType = "test",
                    IdempotencyKey = Guid.NewGuid().ToString(), OccurredAt = DateTimeOffset.UtcNow, NextAttemptAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync(); await transaction.RollbackAsync();
            }
            db.ChangeTracker.Clear();
            Assert.Empty(await db.ProjectIncidents.ToListAsync()); Assert.Empty(await db.AgentPlatformEventOutbox.ToListAsync());
            db.ProjectIncidents.Add(NewIncident()); await db.SaveChangesAsync();
            db.ProjectIncidents.Add(NewIncident()); await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();
            var existing = await db.ProjectIncidents.SingleAsync(); existing.Status = "Resolved"; await db.SaveChangesAsync();
            db.ProjectIncidents.Add(NewIncident()); await db.SaveChangesAsync(); Assert.Equal(2, await db.ProjectIncidents.CountAsync());
            await using var other = new CSweetDbContext(options);
            var concurrent = await other.ProjectIncidents.SingleAsync(x => x.Status == "Open");
            var current = await db.ProjectIncidents.SingleAsync(x => x.Status == "Open");
            current.Revision++; await db.SaveChangesAsync(); concurrent.Revision++;
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => other.SaveChangesAsync());
            Assert.False(db.Database.HasPendingModelChanges());
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    private sealed class HealthPostgresFactAttribute : FactAttribute
    {
        public HealthPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE")))
                Skip = "Set CSWEET_COMPUTE_TEST_DATABASE to a disposable loopback PostgreSQL server.";
        }
    }
}
