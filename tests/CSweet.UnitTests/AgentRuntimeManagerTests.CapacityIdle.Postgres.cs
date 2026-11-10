using System.Data.Common;
using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentRuntimeManagerTests
{
    [CapacityPostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CapacityHandoff_SerializesWithRealInboxClaims(bool claimWins)
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE"));
        Assert.Contains(connection.Host, new[] { "localhost", "127.0.0.1", "::1" });
        connection.Database = "capacity_idle_test_" + Guid.NewGuid().ToString("N");
        connection.Pooling = false;
        await using var owner = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseNpgsql(connection.ConnectionString).Options);
        try
        {
            await owner.Database.EnsureCreatedAsync();
            var source = new AgentPackageSource { Id = Guid.NewGuid(), RepositoryUrl = "https://example.test/capacity" };
            var package = new AgentPackageVersion { Id = Guid.NewGuid(), PackageSourceId = source.Id,
                AgentId = "capacity-fixture", Version = "1.0.0", ManifestJson = "{}", ProjectPath = "src/Agent.csproj",
                Status = AgentPackageVersionStatus.Built, PackageDigest = new string('c', 64), ArtifactSignature = "fixture" };
            AgentInstallation Installation() => new() { Id = Guid.NewGuid(), InstallationKey = Guid.NewGuid(),
                PackageVersionId = package.Id, BusinessId = "business-1", IsEnabled = true,
                Schedule = new() { Id = Guid.NewGuid(), IsEnabled = true, ActivationMode = ActivationMode.OnDemand,
                    MaxRuntimeSeconds = 600 },
                Grant = new() { Id = Guid.NewGuid(), MemoryMb = 512, CpuPercent = 50, MaxRuntimeSeconds = 600 } };
            var idle = Installation(); var next = Installation();
            var donor = RunningInstance(idle.Id); donor.ProviderInstanceId = "idle-vm";
            donor.IdleDeadlineAt = DateTimeOffset.UtcNow.AddMinutes(5);
            var waiting = new AgentRuntimeInstance { Id = Guid.NewGuid(), TickId = Guid.NewGuid(),
                AgentInstallationId = next.Id, QueuedAt = DateTimeOffset.UtcNow };
            // SessionFor only needs the package digest; attach it before constructing the session.
            idle.PackageVersion = package;
            var session = SessionFor(donor, idle, DateTimeOffset.UtcNow.AddMinutes(10));
            owner.AddRange(source, package, idle, next, donor, waiting, session, CapacityWork(next.Id),
                new AgentRuntimeGlobalSettings { Id = Guid.NewGuid(), EnableImportedAgents = true,
                    GlobalMaxActiveWorkloads = 1, PerInstallationMaxActiveWorkloads = 1,
                    WorkloadStopGraceSeconds = 1, RemoveWorkloadsAfterCompletion = true });
            await owner.SaveChangesAsync();

            var protection = new EphemeralDataProtectionProvider();
            var inbox = new AgentWorkInbox(owner, protection, TimeProvider.System);
            var boundary = new CapacityClaimBoundary();
            await using var releaseDb = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
                .UseNpgsql(connection.ConnectionString).AddInterceptors(boundary).Options);
            AgentWorkItem? newWork = null;
            async Task ClaimNewWorkAsync()
            {
                newWork = await inbox.EnqueueAsync(idle.BusinessId, idle.Id, AgentWorkKind.Capability,
                    "example.execute.v1", JsonSerializer.SerializeToElement(new { }), Guid.NewGuid().ToString("N"),
                    DateTimeOffset.UtcNow.AddMinutes(10));
                var claim = await inbox.ClaimAsync(session, default);
                if (claimWins) Assert.Equal(newWork.Id, Assert.IsType<ClaimedAgentWork>(claim).WorkId);
                else Assert.Null(claim);
            }
            var runner = new FakeRunner { BeforeStop = claimWins ? null : _ => ClaimNewWorkAsync() };
            var manager = CreateManager(releaseDb, runner);
            if (claimWins)
            {
                // Pause after donor selection but before the manager acquires the shared claim lock.
                boundary.Arm(skip: 1); // The ordinary warm-idle check runs before blocked admission.
                var release = manager.ReconcileAsync();
                try
                {
                    await boundary.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    await ClaimNewWorkAsync();
                }
                finally { boundary.Continue.TrySetResult(); }
                await release.WaitAsync(TimeSpan.FromSeconds(20));
            }
            else await manager.ReconcileAsync();

            owner.ChangeTracker.Clear();
            var persisted = await owner.AgentRuntimeInstances.SingleAsync(x => x.Id == donor.Id);
            Assert.Equal(claimWins ? AgentRuntimeStatus.Running : AgentRuntimeStatus.Cancelled, persisted.Status);
            Assert.Equal(claimWins ? AgentWorkStatus.Leased : AgentWorkStatus.Pending,
                (await owner.AgentWorkItems.SingleAsync(x => x.Id == newWork!.Id)).Status);
            Assert.Equal(claimWins ? 1 : 0, await owner.AgentWorkAttempts.CountAsync(x => x.AgentWorkItemId == newWork!.Id));
            Assert.Equal(!claimWins, (await owner.McpAgentSessions.SingleAsync()).RevokedAt is not null);
            if (!claimWins)
            {
                await manager.ReconcileAsync();
                Assert.Single(runner.Starts);
            }
            else Assert.Empty(runner.Stops);
        }
        finally { await owner.Database.EnsureDeletedAsync(); }
    }

    private sealed class CapacityClaimBoundary : DbCommandInterceptor
    {
        private bool armed;
        private int skipped;
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Arm(int skip) { armed = true; skipped = skip; }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (armed && command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal))
            {
                if (skipped-- > 0) return result;
                armed = false;
                Reached.TrySetResult();
                await Continue.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }

    private sealed class CapacityPostgresTheoryAttribute : TheoryAttribute
    {
        public CapacityPostgresTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE")))
                Skip = "Set CSWEET_COMPUTE_TEST_DATABASE to a local disposable PostgreSQL endpoint.";
        }
    }
}
