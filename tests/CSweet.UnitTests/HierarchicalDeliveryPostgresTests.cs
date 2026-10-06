using CSweet.Infrastructure.Persistence;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CSweet.UnitTests;

public sealed class HierarchicalDeliveryPostgresTests
{
    [DeliveryPostgresTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FreshGameAndSoftwareDeliverCodeAndDocumentsThroughRelease(bool game, bool artifact)
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE"));
        Assert.Contains(settings.Host, new[] { "localhost", "127.0.0.1", "::1" });
        settings.Database = "delivery_test_" + Guid.NewGuid().ToString("N"); settings.Pooling = false;
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(settings.ConnectionString).Options;
        await using var f = new HierarchicalDeliveryServiceTests.Fixture(artifact, options);
        try
        {
            await f.Db.Database.MigrateAsync(); await f.Seed();
            await HierarchicalTaskRuntimeTests.RunJourney(f, artifact, game);
            for (var pass = 0; pass < 12; pass++)
            {
                await f.Service.PulseAsync(); var plan = await f.Read();
                if (plan.Status == "Completed") break;
                var review = plan.Executions.FirstOrDefault(x => x.Status == "WaitingForHuman");
                if (review is not null) { await f.Approve(review); continue; }
                var acceptance = plan.Executions.FirstOrDefault(x => x.Status == "WaitingForApproval");
                if (acceptance is not null) await f.Accept(acceptance);
            }
            Assert.Equal("Completed", (await f.Read()).Status);
            Assert.All((await f.Read()).Executions, execution => Assert.Equal("Completed", execution.Status));
            Assert.NotEmpty(await f.Db.ApplicationRealtimeOutbox.ToListAsync());
        }
        finally
        {
            Assert.StartsWith("delivery_test_", settings.Database);
            await f.Db.Database.EnsureDeletedAsync();
        }
    }
    [DeliveryPostgresFact]
    public async Task FreshMigrationConcurrentRevisionAndOutboxRollbackAreAtomic()
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE"));
        Assert.Contains(settings.Host, new[] { "localhost", "127.0.0.1", "::1" });
        settings.Database = "delivery_test_" + Guid.NewGuid().ToString("N"); settings.Pooling = false;
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(settings.ConnectionString).Options;
        await using var f = new HierarchicalDeliveryServiceTests.Fixture(true, options);
        try
        {
            await f.Db.Database.MigrateAsync();
            Assert.False(f.Db.Database.HasPendingModelChanges());
            await f.Seed();
            var draft = await f.Configure(); var active = await f.Activate(draft);
            var before = await f.Db.AgentPlatformEventOutbox.CountAsync();
            var request = new ControlWorkDeliveryPlanRequest(active.Id, active.Revision, "pause", "postgres-pause");
            await using (var outer = await f.Db.Database.BeginTransactionAsync())
            {
                f.Db.WorkDeliveryPlans.Single().Name = "Rolled back";
                f.Db.AgentPlatformEventOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org,
                    EventType = WorkDeliveryCapabilities.Changed, DataJson = "{}", IdempotencyKey = "rollback" });
                await f.Db.SaveChangesAsync(); await outer.RollbackAsync();
            }
            f.Db.ChangeTracker.Clear();
            Assert.Equal(before, await f.Db.AgentPlatformEventOutbox.CountAsync());
            Assert.Equal("Initial release", (await f.Read()).Name);
            async Task<bool> Pause(string key)
            {
                await using var concurrent = new HierarchicalDeliveryServiceTests.Fixture(true, options);
                concurrent.Org = f.Org; concurrent.Project = f.Project; concurrent.Manager = f.Manager;
                try { await concurrent.Service.ControlAsync(f.Org, f.Manager.Id, request with { IdempotencyKey = key }); return true; }
                catch (DbUpdateConcurrencyException) { return false; }
                catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.SerializationFailure) { return false; }
            }
            var results = await System.Threading.Tasks.Task.WhenAll(Pause("postgres-first"), Pause("postgres-second"));
            Assert.Single(results, x => x);
            f.Db.ChangeTracker.Clear();
            Assert.Equal("Paused", (await f.Read()).Status);
            Assert.Equal(before + 1, await f.Db.AgentPlatformEventOutbox.CountAsync());
            Assert.Single(await f.Db.WorkDeliveryMutationReceipts.Where(x => x.Operation == "control" && x.IdempotencyKey.StartsWith("postgres-")).ToListAsync());
            Assert.NotEmpty(await f.Db.ApplicationRealtimeOutbox.ToListAsync());
            Assert.NotEmpty(await f.Db.AuditOutbox.ToListAsync());
        }
        finally
        {
            // Only the randomly named database created by this test is removed.
            Assert.StartsWith("delivery_test_", settings.Database);
            await f.Db.Database.EnsureDeletedAsync();
        }
    }
    private sealed class DeliveryPostgresFactAttribute : FactAttribute
    {
        public DeliveryPostgresFactAttribute()
        { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE"))) Skip = "Set CSWEET_COMPUTE_TEST_DATABASE to a loopback PostgreSQL test server."; }
    }
    private sealed class DeliveryPostgresTheoryAttribute : TheoryAttribute
    {
        public DeliveryPostgresTheoryAttribute()
        { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE"))) Skip = "Set CSWEET_COMPUTE_TEST_DATABASE to a loopback PostgreSQL test server."; }
    }
}
