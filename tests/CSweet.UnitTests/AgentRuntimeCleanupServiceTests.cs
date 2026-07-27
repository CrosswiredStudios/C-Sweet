using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class AgentRuntimeCleanupServiceTests
{
    [Fact]
    public async Task CleanupAsync_RemovesDeferredResourcesAndExpiredHistory()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var root = Path.Combine(Path.GetTempPath(), $"csweet-cleanup-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var logPath = Path.Combine(root, "build.log");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "source.txt"), "source");
        await File.WriteAllTextAsync(logPath, "build output");
        try
        {
            var now = DateTimeOffset.UtcNow;
            db.AgentRuntimeGlobalSettings.Add(new AgentRuntimeGlobalSettings
            {
                Id = Guid.NewGuid(), RemoveContainersAfterCompletion = true,
                RemoveWorkspacesAfterCompletion = true, KeepFailedBuildWorkspaces = true,
                CompletedRuntimeRetentionDays = 1, FailedRuntimeRetentionDays = 30, BuildLogRetentionDays = 1,
                AgentSourceRootPath = root, AgentPackageCachePath = root
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
            var expired = CompletedRuntime(installation.Id, now.AddDays(-2), "expired-container");
            var retainedFailure = FailedRuntime(installation.Id, now.AddDays(-2), "failed-container");
            db.AgentRuntimeInstances.AddRange(expired, retainedFailure);
            await db.SaveChangesAsync();
            var runner = new CleanupRunner();
            var audit = new CapturingAuditWriter();
            var service = new AgentRuntimeCleanupService(
                db,
                runner,
                audit,
                Options.Create(new AgentRuntimeManagerOptions
                {
                    DockerNetworkName = "cleanup-network",
                    McpGatewayContainer = "cleanup-gateway"
                }),
                NullLogger<AgentRuntimeCleanupService>.Instance);

            var result = await service.CleanupAsync();

            Assert.Equal(2, result.ContainersRemoved);
            Assert.Equal(1, result.WorkspacesRemoved);
            Assert.Equal(1, result.BuildLogsRemoved);
            Assert.Equal(1, result.RuntimeHistoriesRemoved);
            Assert.Equal(2, runner.NetworkRemoves.Count);
            Assert.All(runner.NetworkRemoves, removal => Assert.Equal("cleanup-gateway", removal.McpGatewayContainer));
            Assert.False(Directory.Exists(workspace));
            Assert.False(File.Exists(logPath));
            Assert.Null(job.SourceWorkspacePath);
            Assert.Null(job.LogPath);
            Assert.Single(await db.AgentRuntimeInstances.ToListAsync());
            Assert.Equal(AgentRuntimeStatus.Failed, (await db.AgentRuntimeInstances.SingleAsync()).Status);
            Assert.Contains("agent-runtime.cleanup.completed", audit.EventTypes);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static AgentRuntimeInstance CompletedRuntime(Guid installationId, DateTimeOffset at, string containerId)
    {
        var runtime = RunningRuntime(installationId, at, containerId);
        runtime.TransitionTo(AgentRuntimeStatus.CompletionReported, at);
        runtime.TransitionTo(AgentRuntimeStatus.Completed, at);
        return runtime;
    }

    private static AgentRuntimeInstance FailedRuntime(Guid installationId, DateTimeOffset at, string containerId)
    {
        var runtime = RunningRuntime(installationId, at, containerId);
        runtime.TransitionTo(AgentRuntimeStatus.Failed, at);
        return runtime;
    }

    private static AgentRuntimeInstance RunningRuntime(Guid installationId, DateTimeOffset at, string containerId)
    {
        var runtime = new AgentRuntimeInstance { Id = Guid.NewGuid(), TickId = Guid.NewGuid(), AgentInstallationId = installationId, QueuedAt = at, ContainerId = containerId, WorkloadTokenHash = new string('0', 64) };
        runtime.TransitionTo(AgentRuntimeStatus.Starting, at);
        runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, at);
        runtime.TransitionTo(AgentRuntimeStatus.Running, at);
        return runtime;
    }

    private sealed class CleanupRunner : IAgentContainerRunner
    {
        public List<(string NetworkName, string McpGatewayContainer)> NetworkRemoves { get; } = [];
        public Task<AgentContainerStatus> StartAsync(AgentContainerStartRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAsync(string containerId, TimeSpan gracePeriod, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AgentContainerStatus?> InspectAsync(string containerId, CancellationToken cancellationToken = default) => Task.FromResult<AgentContainerStatus?>(new(containerId, containerId, AgentContainerState.Exited, 0, null, DateTimeOffset.UtcNow, null));
        public Task<IReadOnlyList<AgentManagedContainer>> ListManagedAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentManagedContainer>>([]);
        public Task RemoveAsync(string containerId, bool force = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveNetworkAsync(string networkName, string brokerGatewayContainer, CancellationToken cancellationToken = default) { NetworkRemoves.Add((networkName, brokerGatewayContainer)); return Task.CompletedTask; }
        public Task<string> GetLogsAsync(string containerId, int maximumBytes, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
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
