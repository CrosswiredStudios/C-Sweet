using CSweet.Application.Setup;
using CSweet.Office.Contracts.Workloads;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class AgentRuntimeCleanupServiceTests : IDisposable
{
    private readonly string logRoot = Path.Combine(Path.GetTempPath(), "csweet-cleanup-logs", Guid.NewGuid().ToString("N"));
    public void Dispose()
    {
        if (Directory.Exists(logRoot)) CSweet.TrustedServices.OwnedStorage.DeleteDirectory(Path.GetDirectoryName(logRoot)!, logRoot);
    }
    [Fact]
    public async Task CleanupAsync_RemovesDeferredResourcesAndExpiredHistory()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var workspace = $"broker-source:{Guid.NewGuid():N}";
        Directory.CreateDirectory(logRoot);
        var logPath = Path.Combine(logRoot, "build.log");
        await File.WriteAllTextAsync(logPath, "retained build diagnostics");
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
                NullLogger<AgentRuntimeCleanupService>.Instance,
                Options.Create(new AgentRuntimeManagerOptions { BuildLogStorePath = logRoot }));

            var result = await service.CleanupAsync();

            Assert.Equal(2, result.WorkloadsRemoved);
            Assert.Equal(1, result.WorkspacesRemoved);
            Assert.Equal(1, result.BuildLogsRemoved);
            Assert.Equal(1, result.RuntimeHistoriesRemoved);
            Assert.Equal(2, runner.Destroyed.Count);
            Assert.Null(job.SourceWorkspacePath);
            Assert.Null(job.LogPath);
            Assert.False(File.Exists(logPath));
            Assert.Single(await db.AgentRuntimeInstances.ToListAsync());
            Assert.Equal(AgentRuntimeStatus.Failed, (await db.AgentRuntimeInstances.SingleAsync()).Status);
            Assert.Contains("agent-runtime.cleanup.completed", audit.EventTypes);
    }

    [Fact]
    public async Task CleanupAsync_RetainsExpiredRuntimeEvidenceWhenDestructionIsNotConfirmed()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.AgentRuntimeGlobalSettings.Add(new AgentRuntimeGlobalSettings
        { Id = Guid.NewGuid(), RemoveWorkloadsAfterCompletion = true, CompletedRuntimeRetentionDays = 1 });
        var instance = CompletedRuntime(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-3), "unconfirmed-vm");
        db.AgentRuntimeInstances.Add(instance);
        await db.SaveChangesAsync();
        var service = new AgentRuntimeCleanupService(db, new CleanupRunner(false), new CapturingAuditWriter(),
            NullLogger<AgentRuntimeCleanupService>.Instance);
        var result = await service.CleanupAsync();
        Assert.Equal(0, result.WorkloadsRemoved);
        Assert.Equal(0, result.RuntimeHistoriesRemoved);
        Assert.Equal("unconfirmed-vm", (await db.AgentRuntimeInstances.SingleAsync()).ProviderInstanceId);
    }

    [Fact]
    public async Task CleanupAsync_RetainsUnsafeLogLocatorAndRetriesAfterStorageConfigurationIsRepaired()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Directory.CreateDirectory(logRoot);
        var path = Path.Combine(logRoot, "build.log");
        await File.WriteAllTextAsync(path, "diagnostic bytes");
        db.AgentRuntimeGlobalSettings.Add(new AgentRuntimeGlobalSettings { Id = Guid.NewGuid(), BuildLogRetentionDays = 1 });
        var now = DateTimeOffset.UtcNow.AddDays(-3);
        var job = new AgentBuildJob { Id = Guid.NewGuid(), PackageVersionId = Guid.NewGuid(), QueuedAt = now, LogPath = path };
        job.TransitionTo(AgentBuildStatus.Cloning, now);
        job.TransitionTo(AgentBuildStatus.Failed, now);
        db.AgentBuildJobs.Add(job);
        await db.SaveChangesAsync();
        var configured = new AgentRuntimeManagerOptions { BuildLogStorePath = Path.Combine(logRoot, "different-store") };
        var service = new AgentRuntimeCleanupService(db, new CleanupRunner(), new CapturingAuditWriter(),
            NullLogger<AgentRuntimeCleanupService>.Instance, Options.Create(configured));
        Assert.Equal(0, (await service.CleanupAsync()).BuildLogsRemoved);
        Assert.Equal(path, job.LogPath);
        Assert.True(File.Exists(path));
        configured.BuildLogStorePath = logRoot;
        Assert.Equal(1, (await service.CleanupAsync()).BuildLogsRemoved);
        Assert.Null(job.LogPath);
        Assert.False(File.Exists(path));
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

    private sealed class CleanupRunner(bool confirmsDestruction = true) : IAgentWorkloadRunner
    {
        public List<IsolationWorkloadHandle> Destroyed { get; } = [];
        public Task<IsolationWorkloadHandle> CreateAndStartAsync(RuntimeWorkloadSpecification workload, AgentTrustLevel trustLevel, string? preferredProviderId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAsync(IsolationWorkloadHandle handle, TimeSpan gracePeriod, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IsolationWorkloadStatus?> InspectAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) => Task.FromResult<IsolationWorkloadStatus?>(new(handle, confirmsDestruction && Destroyed.Contains(handle) ? IsolationWorkloadState.Destroyed : IsolationWorkloadState.Stopped, IsolationTerminationReason.Completed, 0, null, DateTimeOffset.UtcNow, null, null));
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
