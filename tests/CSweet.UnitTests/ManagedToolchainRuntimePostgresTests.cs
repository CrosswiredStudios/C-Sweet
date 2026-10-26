using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CSweet.UnitTests;

public sealed class ManagedToolchainRuntimePostgresTests
{
    [ManagedRuntimePostgresFact]
    public async Task MultipleBuildRuntimesDoNotCollideWithInstallationRuntimeButDuplicateActiveIdentitiesAreRejected()
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE"));
        Assert.Contains(settings.Host, new[] { "localhost", "127.0.0.1", "::1" });
        settings.Database = "managed_runtime_test_" + Guid.NewGuid().ToString("N"); settings.Pooling = false;
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(settings.ConnectionString).Options);
        try
        {
            await db.Database.EnsureCreatedAsync();
            var source = new AgentPackageSource { Id = Guid.NewGuid(), RepositoryUrl = "https://example.test/toolchain" };
            var package = new AgentPackageVersion { Id = Guid.NewGuid(), PackageSourceId = source.Id, AgentId = "test.node", Version = "1.0.1", ManifestJson = "{}" };
            var installation = new AgentInstallation { Id = Guid.NewGuid(), PackageVersionId = package.Id };
            db.AddRange(source, package, installation); await db.SaveChangesAsync();
            var buildIds = Enumerable.Range(0,4).Select(_ => Guid.NewGuid()).ToArray();
            db.AgentRuntimeInstances.Add(Runtime(installation.Id, null));
            foreach (var id in buildIds) db.AgentRuntimeInstances.Add(Runtime(installation.Id, id));
            await db.SaveChangesAsync(); Assert.Equal(5, await db.AgentRuntimeInstances.CountAsync());
            foreach (var buildId in new Guid?[] { null, buildIds[0] })
            {
                var duplicate = Runtime(installation.Id, buildId); db.AgentRuntimeInstances.Add(duplicate);
                var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
                Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
                db.Entry(duplicate).State = EntityState.Detached;
            }
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }
    private static AgentRuntimeInstance Runtime(Guid installationId, Guid? buildId) => new() { Id = Guid.NewGuid(),
        TickId = Guid.NewGuid(), AgentInstallationId = installationId, DeliveryBuildId = buildId, QueuedAt = DateTimeOffset.UtcNow };
    private sealed class ManagedRuntimePostgresFactAttribute : FactAttribute
    {
        public ManagedRuntimePostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE")))
                Skip = "Requires a loopback PostgreSQL endpoint; creates and deletes its own disposable database.";
        }
    }
}
