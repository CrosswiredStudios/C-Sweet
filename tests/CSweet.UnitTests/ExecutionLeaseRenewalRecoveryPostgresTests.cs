using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class ExecutionWorkloadOrchestratorTests
{
    [FleetRecoveryPostgresTheory]
    [InlineData("token-rotation", true)]
    [InlineData("fenced", false)]
    [InlineData("expired", false)]
    public async Task PostgresRenewalRaceUsesCurrentAuthorityWithoutLosingOtherAssignments(string race, bool expected)
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE"));
        Assert.Contains(settings.Host, new[] { "localhost", "127.0.0.1", "::1" });
        settings.Database = "fleet_recovery_test_" + Guid.NewGuid().ToString("N");
        settings.Pooling = false;
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(settings.ConnectionString).Options;
        await using var owner = new CSweetDbContext(options);
        try
        {
            await owner.Database.EnsureCreatedAsync();
            var pool = Pool();
            var node = Node(pool, Guid.NewGuid());
            var target = Assignment(pool.Id, node.Id, 1, 512);
            var other = Assignment(pool.Id, node.Id, 1, 512);
            var source = new AgentPackageSource { Id = Guid.NewGuid(), RepositoryUrl = "https://example.test/fleet-recovery" };
            var package = new AgentPackageVersion { Id = Guid.NewGuid(), PackageSourceId = source.Id,
                AgentId = "fleet-recovery-test", Version = "1.0.0", ManifestJson = "{}" };
            owner.AddRange(source, package, pool, node, target, other,
                new AgentBuildJob { Id = target.AgentBuildJobId!.Value, PackageVersionId = package.Id, Attempt = 1 },
                new AgentBuildJob { Id = other.AgentBuildJobId!.Value, PackageVersionId = package.Id, Attempt = 2 });
            await owner.SaveChangesAsync();
            var interceptor = new RenewalRace(options, target.Id, race);
            await using var worker = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>(options)
                .AddInterceptors(interceptor).Options);
            var scheduler = new ExecutionWorkloadOrchestrator(worker, new MutableTimeProvider(Now));

            Assert.Equal(expected, await scheduler.RenewLeaseAsync(node.Id, target.Id, target.FencingEpoch));
            Assert.Equal(1, interceptor.Conflicts);
            Assert.True(await scheduler.RenewLeaseAsync(node.Id, other.Id, other.FencingEpoch));
            owner.ChangeTracker.Clear();
            var current = await owner.ExecutionWorkloadAssignments.SingleAsync(x => x.Id == target.Id);
            Assert.Equal(expected ? Now.AddSeconds(60) : race == "expired" ? Now.AddSeconds(-1) : Now.AddSeconds(30), current.LeaseExpiresAt);
            Assert.Equal(Now.AddSeconds(60), (await owner.ExecutionWorkloadAssignments.SingleAsync(x => x.Id == other.Id)).LeaseExpiresAt);
        }
        finally { await owner.Database.EnsureDeletedAsync(); }
    }

    private sealed class FleetRecoveryPostgresTheoryAttribute : TheoryAttribute
    {
        public FleetRecoveryPostgresTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE")))
                Skip = "Set CSWEET_COMPUTE_TEST_DATABASE to a loopback PostgreSQL endpoint with database creation permission; each case creates and deletes its own database.";
        }
    }
}
