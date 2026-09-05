using CSweet.Application.SourceControl;
using CSweet.Contracts.SourceControl;
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

    [Theory]
    [InlineData("inspect")]
    [InlineData("publish")]
    [InlineData("refresh")]
    [InlineData("cleanup")]
    public async Task InternalOperationsResolvePersistedAuthorityAndRetainDirtyCleanup(string operation)
    {
        await using var db = CreateDb();
        var request = await SeedOperationAsync(db, operation);
        var host = new FakeHost(); var volumes = new FakeVolumes();
        var result = await new AgentWorkspaceBroker(db, host, volumes).ExecuteAsync(request, "http://localhost:5097");
        Assert.Equal(request.WorkspaceId, host.Operation!.WorkspaceId);
        Assert.Equal(request.RepositoryId, host.Operation.RepositoryId);
        Assert.Equal("csweet/safe-work", host.Operation.Branch);
        Assert.Equal(new string('a', 40), host.Operation.BaseSha);
        if (operation == "cleanup") { Assert.Equal("Retained", result.Status); Assert.False(volumes.Removed); }
        if (operation == "publish") Assert.Contains("source-control?repository=", result.ReviewUrl);
    }

    [Theory]
    [InlineData("assignment")]
    [InlineData("policy")]
    [InlineData("membership")]
    [InlineData("identity")]
    public async Task RevokedWorkspaceAuthorityRejectsBeforeSnapshotExport(string revoked)
    {
        await using var db = CreateDb();
        var request = await SeedOperationAsync(db, "publish");
        if (revoked == "assignment") (await db.CoreWorkTasks.SingleAsync()).AssignmentRevision++;
        if (revoked == "policy") (await db.TeamRepositoryPolicies.SingleAsync()).DisabledAt = DateTimeOffset.UtcNow;
        if (revoked == "membership") (await db.TeamMemberships.SingleAsync()).EndedAt = DateTimeOffset.UtcNow;
        if (revoked == "identity") (await db.CoreOrganizationUsers.SingleAsync()).IsActive = false;
        await db.SaveChangesAsync();
        var host = new FakeHost(); var volumes = new FakeVolumes();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new AgentWorkspaceBroker(db, host, volumes).ExecuteAsync(request, "http://localhost"));
        Assert.Null(host.Operation); Assert.Null(volumes.Lease);
    }

    [Fact]
    public async Task RefreshDoesNotReplaceDirtyWorkspaceWhenRemoteChanged()
    {
        await using var db = CreateDb();
        var request = await SeedOperationAsync(db, "refresh");
        var host = new FakeHost { LatestSha = new string('c', 40) }; var volumes = new FakeVolumes();
        var result = await new AgentWorkspaceBroker(db, host, volumes).ExecuteAsync(request, "http://localhost");
        Assert.Equal("Conflict", result.Status); Assert.Null(volumes.Manifest);
        Assert.Equal(new string('a', 40), result.BaseSha);
    }

    [Fact]
    public async Task CleanupRetryCompletesWhenSnapshotWasAlreadyRemoved()
    {
        await using var db = CreateDb(); var request = await SeedOperationAsync(db, "cleanup");
        var host = new FakeHost(); var volumes = new FakeVolumes { Missing = true };
        var result = await new AgentWorkspaceBroker(db, host, volumes).ExecuteAsync(request, "http://localhost");
        Assert.True(result.Removed); Assert.True(volumes.Removed); Assert.Null(host.Operation);
    }

    [Fact]
    public async Task RefreshRetryRecognizesSnapshotAlreadyAtLatestSha()
    {
        await using var db = CreateDb(); var request = await SeedOperationAsync(db, "refresh");
        var host = new FakeHost { LatestSha = new string('c', 40), CleanAtLatest = true }; var volumes = new FakeVolumes();
        var result = await new AgentWorkspaceBroker(db, host, volumes).ExecuteAsync(request, "http://localhost");
        Assert.Equal("Refreshed", result.Status); Assert.Equal(host.LatestSha, result.BaseSha); Assert.Null(volumes.Manifest);
    }

    private static async Task<AgentBrokerWorkspaceOperationRequest> SeedOperationAsync(CSweetDbContext db, string operation)
    {
        await SeedAsync(db);
        var workspace = await db.SourceControlWorkspaces.SingleAsync();
        workspace.Status = SourceControlWorkspaceStatus.Ready; workspace.BaseCommitSha = new string('a', 40); workspace.WorkspaceKey = "opaque";
        (await db.SourceControlConnections.SingleAsync()).Provider = SourceControlProvider.InternalGit;
        var employee = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = workspace.OrganizationId,
            AgentInstallationId = workspace.AgentInstallationId, IsActive = true, DisplayName = "Developer" };
        db.CoreOrganizationUsers.Add(employee);
        db.OrganizationTeams.Add(new() { Id = workspace.TeamId, OrganizationId = workspace.OrganizationId, Name = "Development" });
        db.TeamMemberships.Add(new() { Id = Guid.NewGuid(), OrganizationId = workspace.OrganizationId, TeamId = workspace.TeamId, OrganizationUserId = employee.Id });
        db.TeamRepositoryPolicies.Add(new() { Id = Guid.NewGuid(), OrganizationId = workspace.OrganizationId, TeamId = workspace.TeamId, RepositoryId = workspace.RepositoryId });
        await db.SaveChangesAsync();
        return new(workspace.OrganizationId, workspace.RepositoryId, workspace.Id, workspace.WorkItemId, workspace.AssignmentRevision,
            workspace.WorkspaceKey, "operation-1", operation);
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
        public InternalGitSnapshotOperation? Operation { get; private set; }
        public bool CleanAtLatest { get; set; }
        public string? LatestSha { get; set; }
        public Task<InternalGitSnapshotResult> ApplyInternalSnapshotAsync(InternalGitSnapshotOperation request, CancellationToken cancellationToken = default)
        {
            Operation = request;
            return Task.FromResult(new InternalGitSnapshotResult("Published", request.BaseSha, new string('b', 40), CleanAtLatest && request.BaseSha == LatestSha ? [] : ["README.md"], "1 file changed", LatestSha));
        }
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
        public bool Missing { get; set; }
        public bool Removed { get; private set; }
        public Task RemoveAsync(WorkspaceVolumeLease lease, CancellationToken cancellationToken = default) { Removed = true; return Task.CompletedTask; }
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
            CancellationToken cancellationToken = default)
        { if (Missing) throw new WorkspaceSnapshotUnavailableException(); Lease = lease; return Task.FromResult(new WorkspaceVolumeExport([1, 2, 3, 4], new(new string('b', 64), 1, 4))); }
    }
}
