using CSweet.Application.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.SourceControl;
using CSweet.TrustedServices;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class AgentWorkspaceBrokerTests
{
    [Fact]
    public async Task PrepareResolvesProviderAuthorityInsideCoreAndReturnsOnlyWorkspaceMetadata()
    {
        await using var db = CreateDb();
        var seeded = await SeedAsync(db);
        var host = new FakeHost();
        var volumes = new FakeVolumes();
        var broker = new AgentWorkspaceBroker(db, host, volumes);

        var result = await broker.PrepareAsync(seeded.Request);

        Assert.Equal("workspace-opaque", result.WorkspaceKey);
        Assert.Equal($"/workspace/{seeded.Request.WorkItemId:N}/1", result.AgentWorkspacePath);
        Assert.Equal(71234, host.Request!.InstallationId);
        Assert.Equal("private-owner", host.Request.Owner);
        Assert.Equal("private-repository", host.Request.Repository);
        Assert.Equal(seeded.Request.AgentInstallationId, volumes.Lease!.AgentInstallationId);
        Assert.Equal(new WorkspaceArtifactManifest(new string('b', 64), 1, 4), volumes.Manifest);
    }

    [Fact]
    public async Task StaleAssignmentIsRejectedBeforeGitHostOrDocker()
    {
        await using var db = CreateDb();
        var seeded = await SeedAsync(db);
        var workItem = await db.CoreWorkTasks.SingleAsync();
        workItem.AssignmentRevision = 2;
        await db.SaveChangesAsync();
        var host = new FakeHost();
        var volumes = new FakeVolumes();
        var broker = new AgentWorkspaceBroker(db, host, volumes);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => broker.PrepareAsync(seeded.Request));

        Assert.Null(host.Request);
        Assert.Null(volumes.Lease);
    }

    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);

    private static async Task<Seeded> SeedAsync(CSweetDbContext db)
    {
        var organizationId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var workItemId = Guid.NewGuid();
        var connection = new SourceControlConnection
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Provider = SourceControlProvider.GitHub,
            Mode = SourceControlConnectionMode.ExistingGitHub,
            Status = SourceControlConnectionStatus.Connected,
            SourceAccessInstallationId = 71234,
            Name = "Private GitHub"
        };
        var repository = new SourceControlRepository
        {
            Id = repositoryId,
            OrganizationId = organizationId,
            ConnectionId = connection.Id,
            Owner = "private-owner",
            Name = "private-repository",
            CanonicalPath = "private-owner/private-repository",
            CloneUrl = "https://github.com/private-owner/private-repository.git",
            DefaultBranch = "main",
            IsPrivate = true,
            Status = SourceControlRepositoryStatus.Ready,
            Connection = connection
        };
        var installation = new AgentInstallation
        {
            Id = installationId,
            InstallationKey = Guid.NewGuid(),
            PackageVersionId = Guid.NewGuid(),
            BusinessId = organizationId.ToString("D"),
            IsEnabled = true
        };
        var workItem = new WorkTask
        {
            Id = workItemId,
            OrganizationId = organizationId,
            AssignedAgentInstallationId = installationId,
            AssignmentRevision = 1,
            Title = "Build safely"
        };
        var workspace = new SourceControlWorkspace
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            TeamId = Guid.NewGuid(),
            RepositoryId = repositoryId,
            AgentInstallationId = installationId,
            WorkItemId = workItemId,
            AssignmentRevision = 1,
            WorkspaceKey = string.Empty,
            BranchName = "csweet/safe-work",
            BaseCommitSha = string.Empty,
            Status = SourceControlWorkspaceStatus.Preparing,
            Repository = repository
        };
        db.AddRange(connection, repository, installation, workItem, workspace);
        await db.SaveChangesAsync();
        return new Seeded(new AgentBrokerWorkspacePrepareRequest(
            organizationId,
            installationId,
            repositoryId,
            workspace.Id,
            workItemId,
            1,
            workspace.BranchName,
            null,
            "prepare-safe-1"));
    }

    private sealed record Seeded(AgentBrokerWorkspacePrepareRequest Request);

    private sealed class FakeHost : ITrustedSourceControlHostClient
    {
        public TrustedWorkspaceSnapshotRequest? Request { get; private set; }

        public Task<TrustedWorkspaceSnapshot> PrepareWorkspaceAsync(
            TrustedWorkspaceSnapshotRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new TrustedWorkspaceSnapshot(
                "workspace-opaque",
                new string('a', 40),
                false,
                [1, 2, 3, 4],
                new string('b', 64),
                1,
                4));
        }

        public Task<TrustedInstallationDescriptor> DescribeInstallationAsync(long installationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TrustedRepositoryDescriptor>> ListRepositoriesAsync(long installationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TrustedMergeResult> MergeAsync(TrustedMergeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeVolumes : IWorkspaceVolumeBridge
    {
        public WorkspaceVolumeLease? Lease { get; private set; }
        public WorkspaceArtifactManifest? Manifest { get; private set; }

        public Task<WorkspaceArtifactManifest> ImportAsync(
            WorkspaceVolumeLease lease,
            Stream archive,
            WorkspaceArtifactManifest? expectedManifest = null,
            CancellationToken cancellationToken = default)
        {
            Lease = lease;
            Manifest = expectedManifest;
            return Task.FromResult(expectedManifest!);
        }

        public Task<WorkspaceVolumeExport> ExportAsync(
            WorkspaceVolumeLease lease,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
