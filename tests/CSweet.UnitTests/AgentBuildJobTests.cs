using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed class AgentBuildJobTests
{
    [Fact]
    public void TransitionTo_RecordsValidLifecycleTimestamps()
    {
        var queuedAt = DateTimeOffset.UtcNow;
        var job = new AgentBuildJob { Id = Guid.NewGuid(), QueuedAt = queuedAt };

        job.TransitionTo(AgentBuildStatus.Cloning, queuedAt.AddSeconds(1));
        job.TransitionTo(AgentBuildStatus.Building, queuedAt.AddSeconds(2));
        job.TransitionTo(AgentBuildStatus.Succeeded, queuedAt.AddSeconds(3));

        Assert.Equal(AgentBuildStatus.Succeeded, job.Status);
        Assert.Equal(queuedAt.AddSeconds(1), job.StartedAt);
        Assert.Equal(queuedAt.AddSeconds(3), job.CompletedAt);
    }

    [Fact]
    public void TransitionTo_RejectsInvalidLifecycleTransition()
    {
        var job = new AgentBuildJob { Id = Guid.NewGuid(), QueuedAt = DateTimeOffset.UtcNow };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            job.TransitionTo(AgentBuildStatus.Succeeded, DateTimeOffset.UtcNow));

        Assert.Contains("Queued to Succeeded", exception.Message);
    }

    [Fact]
    public async Task ProcessNextAsync_PersistsImmutablePackageAndSuccessfulTransitions()
    {
        await using var dbContext = CreateDbContext();
        var (package, job) = await SeedAsync(dbContext);
        var executor = new FakeBuildExecutor();
        var service = CreateService(dbContext, executor);

        var processed = await service.ProcessNextAsync();

        Assert.True(processed);
        Assert.Equal(AgentBuildStatus.Succeeded, job.Status);
        Assert.Equal(AgentPackageVersionStatus.Built, package.Status);
        Assert.Equal(FakeBuildExecutor.Digest, package.PackageDigest);
        Assert.Equal(FakeBuildExecutor.PackagePath, package.PackagePath);
        Assert.NotNull(package.BuiltAt);
        Assert.True(executor.CleanupCalled);
        var steps = AgentBuildStepStoreForTest.Read(job.StepsJson);
        Assert.Equal(6, steps.Count);
        Assert.All(steps, step => Assert.Equal(AgentBuildStepStatuses.Succeeded, step.Status));
    }

    [Fact]
    public async Task ProcessNextAsync_MarksFailedBuildAndRetainsConfiguredWorkspace()
    {
        await using var dbContext = CreateDbContext();
        var (package, job) = await SeedAsync(dbContext, keepFailedWorkspace: true);
        var executor = new FakeBuildExecutor { BuildFailure = new AgentBuildException("publish failed") };
        var service = CreateService(dbContext, executor);

        var processed = await service.ProcessNextAsync();

        Assert.True(processed);
        Assert.Equal(AgentBuildStatus.Failed, job.Status);
        Assert.Equal(AgentPackageVersionStatus.Failed, package.Status);
        Assert.Equal("publish failed", job.FailureMessage);
        Assert.False(executor.CleanupCalled);
        Assert.Contains(
            AgentBuildStepStoreForTest.Read(job.StepsJson),
            step => step.Key == AgentBuildStepKeys.Publish &&
                    step.Status == AgentBuildStepStatuses.Failed &&
                    step.Error == "publish failed");
    }

    [Fact]
    public async Task ProcessNextAsync_RetriesTransientSourceFailureAndEventuallyBuilds()
    {
        await using var dbContext = CreateDbContext();
        var (package, firstJob) = await SeedAsync(dbContext);
        var executor = new FakeBuildExecutor();
        executor.CloneFailures.Enqueue(new AgentBuildException(
            "Required command 'git' failed: Recv failure: Connection was reset"));
        var service = CreateService(dbContext, executor);

        Assert.True(await service.ProcessNextAsync());

        Assert.Equal(AgentBuildStatus.Failed, firstJob.Status);
        Assert.Equal(AgentPackageVersionStatus.Approved, package.Status);
        var retry = await dbContext.AgentBuildJobs.SingleAsync(x => x.Attempt == 2);
        Assert.Equal(AgentBuildStatus.Queued, retry.Status);
        retry.QueuedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await dbContext.SaveChangesAsync();

        Assert.True(await service.ProcessNextAsync());

        Assert.Equal(AgentBuildStatus.Succeeded, retry.Status);
        Assert.Equal(AgentPackageVersionStatus.Built, package.Status);
        Assert.Equal(2, executor.CloneCalls);
    }

    [Fact]
    public async Task ProcessNextAsync_StopsAfterThreeTransientSourceFailures()
    {
        await using var dbContext = CreateDbContext();
        var (package, _) = await SeedAsync(dbContext);
        var executor = new FakeBuildExecutor();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            executor.CloneFailures.Enqueue(new AgentBuildException(
                "fatal: unable to access repository: Could not resolve host"));
        }
        var service = CreateService(dbContext, executor);

        Assert.True(await service.ProcessNextAsync());
        var retry = await dbContext.AgentBuildJobs.SingleAsync(x => x.Attempt == 2);
        retry.QueuedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await dbContext.SaveChangesAsync();
        Assert.True(await service.ProcessNextAsync());
        retry = await dbContext.AgentBuildJobs.SingleAsync(x => x.Attempt == 3);
        retry.QueuedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await dbContext.SaveChangesAsync();
        Assert.True(await service.ProcessNextAsync());

        Assert.Equal(AgentPackageVersionStatus.Failed, package.Status);
        var jobs = await dbContext.AgentBuildJobs.OrderBy(x => x.Attempt).ToListAsync();
        Assert.Equal(3, jobs.Count);
        Assert.All(jobs, job => Assert.Equal(AgentBuildStatus.Failed, job.Status));
    }

    [Fact]
    public async Task QueueAsync_IsIdempotentWhileBuildIsActiveAndCreatesRetryAfterFailure()
    {
        await using var dbContext = CreateDbContext();
        var (package, job) = await SeedAsync(dbContext);
        var service = CreateService(dbContext, new FakeBuildExecutor());

        var existingId = await service.QueueAsync(package.Id);
        job.TransitionTo(AgentBuildStatus.Failed, DateTimeOffset.UtcNow);
        package.Status = AgentPackageVersionStatus.Failed;
        await dbContext.SaveChangesAsync();
        var retryId = await service.QueueAsync(package.Id);

        Assert.Equal(job.Id, existingId);
        Assert.NotEqual(job.Id, retryId);
        Assert.Equal(2, await dbContext.AgentBuildJobs.CountAsync());
        Assert.Equal(2, (await dbContext.AgentBuildJobs.SingleAsync(x => x.Id == retryId)).Attempt);
    }

    [Fact]
    public async Task ProcessNextAsync_CancelsQueuedJobWhenPackageWasRevoked()
    {
        await using var dbContext = CreateDbContext();
        var (package, job) = await SeedAsync(dbContext);
        package.Status = AgentPackageVersionStatus.Revoked;
        await dbContext.SaveChangesAsync();
        var executor = new FakeBuildExecutor();
        var service = CreateService(dbContext, executor);

        var processed = await service.ProcessNextAsync();

        Assert.True(processed);
        Assert.Equal(AgentBuildStatus.Cancelled, job.Status);
        Assert.Equal(AgentPackageVersionStatus.Revoked, package.Status);
        Assert.False(executor.CloneCalled);
    }

    private static AgentBuildService CreateService(
        CSweetDbContext dbContext,
        IAgentBuildExecutor executor) =>
        new(dbContext, executor, new TestAuditEventWriter(), NullLogger<AgentBuildService>.Instance);

    private static async Task<(AgentPackageVersion Package, AgentBuildJob Job)> SeedAsync(
        CSweetDbContext dbContext,
        bool keepFailedWorkspace = false)
    {
        var source = new AgentPackageSource
        {
            Id = Guid.NewGuid(),
            RepositoryUrl = "https://github.com/example/agent",
            RepositoryOwner = "example",
            RepositoryName = "agent",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var package = new AgentPackageVersion
        {
            Id = Guid.NewGuid(),
            PackageSourceId = source.Id,
            PackageSource = source,
            CommitSha = "0123456789abcdef0123456789abcdef01234567",
            ManifestDigest = new string('a', 64),
            ManifestJson = "{}",
            AgentId = "com.example.agent",
            AgentName = "Example Agent",
            Version = "1.0.0",
            PublisherId = "com.example",
            PublisherName = "Example",
            RuntimeType = "dotnet-project",
            ProjectPath = "src/Agent/Agent.csproj",
            TargetFramework = "net10.0",
            Status = AgentPackageVersionStatus.Approved,
            ImportedAt = DateTimeOffset.UtcNow
        };
        var job = new AgentBuildJob
        {
            Id = Guid.NewGuid(),
            PackageVersionId = package.Id,
            PackageVersion = package,
            Attempt = 1,
            QueuedAt = DateTimeOffset.UtcNow
        };
        dbContext.AgentRuntimeGlobalSettings.Add(new AgentRuntimeGlobalSettings
        {
            Id = Guid.NewGuid(),
            BuildTimeoutSeconds = 60,
            BuildMemoryMb = 1024,
            BuildCpuPercent = 100,
            DefaultWorkloadProcessLimit = 100,
            MaximumRepositorySizeMb = 10,
            MaximumBuildLogMb = 1,
            KeepFailedBuildWorkspaces = keepFailedWorkspace,
            RemoveWorkspacesAfterCompletion = true,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        dbContext.AgentPackageSources.Add(source);
        dbContext.AgentPackageVersions.Add(package);
        dbContext.AgentBuildJobs.Add(job);
        await dbContext.SaveChangesAsync();
        return (package, job);
    }

    private static CSweetDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new CSweetDbContext(options);
    }

    private sealed class FakeBuildExecutor : IAgentBuildExecutor
    {
        public const string Digest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        public const string PackagePath = "/packages/version/digest";
        public Exception? BuildFailure { get; init; }
        public Queue<Exception> CloneFailures { get; } = new();
        public bool CleanupCalled { get; private set; }
        public bool CloneCalled { get; private set; }
        public int CloneCalls { get; private set; }

        public Task<AgentBuildWorkspace> CloneAsync(
            AgentBuildExecutionRequest request,
            IAgentBuildProgressReporter progress,
            CancellationToken cancellationToken = default)
        {
            CloneCalled = true;
            CloneCalls++;
            if (CloneFailures.TryDequeue(out var failure))
            {
                throw failure;
            }
            return Task.FromResult(new AgentBuildWorkspace("/sources/job", "/packages/.staging/job", "/logs/job.log"));
        }

        public async Task<AgentBuildExecutionResult> BuildAsync(
            AgentBuildExecutionRequest request,
            AgentBuildWorkspace workspace,
            IAgentBuildProgressReporter progress,
            CancellationToken cancellationToken = default)
        {
            await progress.ReportAsync(
                new AgentBuildProgressUpdate(
                    AgentBuildStepKeys.Restore,
                    AgentBuildStepStatuses.InProgress),
                cancellationToken);
            await progress.ReportAsync(
                new AgentBuildProgressUpdate(
                    AgentBuildStepKeys.Restore,
                    AgentBuildStepStatuses.Succeeded),
                cancellationToken);
            await progress.ReportAsync(
                new AgentBuildProgressUpdate(
                    AgentBuildStepKeys.Publish,
                    AgentBuildStepStatuses.InProgress),
                cancellationToken);
            if (BuildFailure is not null)
            {
                throw BuildFailure;
            }
            await progress.ReportAsync(
                new AgentBuildProgressUpdate(
                    AgentBuildStepKeys.Publish,
                    AgentBuildStepStatuses.Succeeded),
                cancellationToken);
            return new AgentBuildExecutionResult(PackagePath, Digest, workspace.LogPath);
        }

        public Task CleanupWorkspaceAsync(
            AgentBuildWorkspace workspace,
            CancellationToken cancellationToken = default)
        {
            CleanupCalled = true;
            return Task.CompletedTask;
        }
    }

    private static class AgentBuildStepStoreForTest
    {
        public static IReadOnlyList<CSweet.Contracts.Agents.AgentBuildStepResponse> Read(string json) =>
            System.Text.Json.JsonSerializer.Deserialize<IReadOnlyList<CSweet.Contracts.Agents.AgentBuildStepResponse>>(
                json,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? [];
    }
}
