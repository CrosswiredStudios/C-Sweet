using CSweet.Application.Setup;
using CSweet.SatelliteOffice.Contracts.Workloads;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed class AgentRuntimeCleanupServiceTests
{
    [Fact]
    public async Task CleanupAsync_RemovesDeferredResourcesAndExpiredHistory()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var workspace = $"broker-source:{Guid.NewGuid():N}";
        var logPath = $"broker-log:{Guid.NewGuid():N}";
            var now = DateTimeOffset.UtcNow;
            db.AgentRuntimeGlobalSettings.Add(new AgentRuntimeGlobalSettings
            {
                Id = Guid.NewGuid(), RemoveWorkloadsAfterCompletion = true,
                RemoveWorkspacesAfterCompletion = true, KeepFailedBuildWorkspaces = true,
                CompletedRuntimeRetentionDays = 1, FailedRuntimeRetentionDays = 30, BuildLogRetentionDays = 1
            });
            var package = new AgentPackageVersion
            {
                Id = Guid.NewGuid(), PackageSourceId = Guid.NewGuid(), CommitSha = new string('a', 40),
                ManifestDigest = new string('b', 64), ManifestJson = "{}", AgentId = "agent", AgentName = "Agent",
                Version = "1", PublisherId = "publisher", PublisherName = "Publisher", RuntimeType = "dotnet-project",
                WarningsJson = "[]", ImportedAt = now
            };
            var installation = new AgentInstallation { Id = Guid.NewGuid(), PackageVersionId = package.Id, BusinessId = "business", PackageVersion = package };
            db.AgentInstallations.Add(installation);
            var job = new AgentBuildJob { Id = Guid.NewGuid(), PackageVersionId = package.Id, PackageVersion = package, QueuedAt = now.AddDays(-3), SourceWorkspacePath = workspace, LogPath = logPath };
            job.TransitionTo(AgentBuildStatus.Cloning, now.AddDays(-3));
            job.TransitionTo(AgentBuildStatus.Failed, now.AddDays(-2));
            db.AgentBuildJobs.Add(job);
            var expired = CompletedRuntime(installation.Id, now.AddDays(-2), "expired-provider-instance");
            var retainedFailure = FailedRuntime(installation.Id, now.AddDays(-2), "failed-provider-instance");
            db.AgentRuntimeInstances.AddRange(expired, retainedFailure);
            await db.SaveChangesAsync();
            var runner = new CleanupRunner();
            var audit = new CapturingAuditWriter();
            var service = new AgentRuntimeCleanupService(
                db,
                runner,
                audit,
                NullLogger<AgentRuntimeCleanupService>.Instance);

            var result = await service.CleanupAsync();

            Assert.Equal(2, result.WorkloadsRemoved);
            Assert.Equal(1, result.WorkspacesRemoved);
            Assert.Equal(1, result.BuildLogsRemoved);
            Assert.Equal(1, result.RuntimeHistoriesRemoved);
            Assert.Equal(2, runner.Destroyed.Count);
            Assert.Null(job.SourceWorkspacePath);
            Assert.Null(job.LogPath);
            Assert.Single(await db.AgentRuntimeInstances.ToListAsync());
            Assert.Equal(AgentRuntimeStatus.Failed, (await db.AgentRuntimeInstances.SingleAsync()).Status);
            Assert.Contains("agent-runtime.cleanup.completed", audit.EventTypes);
    }

    private static AgentRuntimeInstance CompletedRuntime(Guid installationId, DateTimeOffset at, string providerInstanceId)
    {
        var runtime = RunningRuntime(installationId, at, providerInstanceId);
        runtime.TransitionTo(AgentRuntimeStatus.CompletionReported, at);
        runtime.TransitionTo(AgentRuntimeStatus.Completed, at);
        return runtime;
    }

    private static AgentRuntimeInstance FailedRuntime(Guid installationId, DateTimeOffset at, string providerInstanceId)
    {
        var runtime = RunningRuntime(installationId, at, providerInstanceId);
        runtime.TransitionTo(AgentRuntimeStatus.Failed, at);
        return runtime;
    }

    private static AgentRuntimeInstance RunningRuntime(Guid installationId, DateTimeOffset at, string providerInstanceId)
    {
        var runtime = new AgentRuntimeInstance { Id = Guid.NewGuid(), TickId = Guid.NewGuid(), AgentInstallationId = installationId, QueuedAt = at, IsolationProviderId = "test-vm", ProviderInstanceId = providerInstanceId, BrokerTokenHash = new string('0', 64) };
        runtime.TransitionTo(AgentRuntimeStatus.Starting, at);
        runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, at);
        runtime.TransitionTo(AgentRuntimeStatus.Running, at);
        return runtime;
    }

    private sealed class CleanupRunner : IAgentWorkloadRunner
    {
        public List<IsolationWorkloadHandle> Destroyed { get; } = [];
        public Task<IsolationWorkloadHandle> CreateAndStartAsync(RuntimeWorkloadSpecification workload, AgentTrustLevel trustLevel, string? preferredProviderId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAsync(IsolationWorkloadHandle handle, TimeSpan gracePeriod, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IsolationWorkloadStatus?> InspectAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) => Task.FromResult<IsolationWorkloadStatus?>(new(handle, IsolationWorkloadState.Stopped, IsolationTerminationReason.Completed, 0, null, DateTimeOffset.UtcNow, null, null));
        public Task DestroyAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) { Destroyed.Add(handle); return Task.CompletedTask; }
        public Task<string> GetLogsAsync(IsolationWorkloadHandle handle, int maximumBytes, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
    }

    private sealed class CapturingAuditWriter : IAuditEventWriter
    {
        public List<string> EventTypes { get; } = [];
        public Task WriteAsync(string eventType, string entityType, Guid? entityId, string? summary, string? metadataJson = null, CancellationToken cancellationToken = default)
        {
            EventTypes.Add(eventType);
            return Task.CompletedTask;
        }
    }
}
