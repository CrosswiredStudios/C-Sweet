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
    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task PreparationRejectsMissingGitHubIdentityBeforeHostOrVolumeAccess(string identity)
    {
        await using var db = CreateDb(); var seeded = await SeedAsync(db);
        (await db.SourceControlRepositories.SingleAsync()).ExternalRepositoryId = identity;
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var host = new FakeHost(); var volumes = new FakeVolumes();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AgentWorkspaceBroker(db, host, volumes).PrepareAsync(seeded.Request));
        Assert.Null(host.Request); Assert.Null(volumes.Lease);
    }

    [Fact]
    public async Task OfflineBrokerPublishesRealGitAndLfsThenMergesExactCommit()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-offline-broker", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, ".csweet-git-store"), "offline-test");
        try
        {
            var artifacts = new WorkspaceArtifactValidator();
            var store = new InternalGitRepositoryStore(Microsoft.Extensions.Options.Options.Create(new InternalGitStorageOptions {
                RepositoryRoot = root, ExpectedStoreId = "offline-test", TemporaryRoot = Path.Combine(root, "operations") }));
            await using var db = CreateDb(); var seeded = await SeedAsync(db); var request = seeded.Request;
            (await db.SourceControlConnections.SingleAsync()).Provider = SourceControlProvider.InternalGit;
            await db.SaveChangesAsync();
            await store.ExecuteAsync(new(request.OrganizationId, request.RepositoryId, "create", "main"));
            var host = new FakeHost { NativeStore = store }; var volumes = new FakeVolumes();
            var broker = new AgentWorkspaceBroker(db, host, volumes);
            var prepared = await broker.PrepareAsync(request);
            var workspace = await db.SourceControlWorkspaces.SingleAsync();
            workspace.Status = SourceControlWorkspaceStatus.Ready; workspace.BaseCommitSha = prepared.BaseCommitSha; workspace.WorkspaceKey = prepared.WorkspaceKey;
            await db.SaveChangesAsync();
            var input = Path.Combine(root, "input"); Directory.CreateDirectory(input);
            await File.WriteAllTextAsync(Path.Combine(input, "README.md"), "Offline feature");
            await File.WriteAllTextAsync(Path.Combine(input, ".gitattributes"), "*.bin filter=lfs diff=lfs merge=lfs -text\n");
            await File.WriteAllBytesAsync(Path.Combine(input, "asset.bin"), [0, 1, 2, 255]);
            using var output = new MemoryStream(); var manifest = await artifacts.CreateZipAsync(input, output);
            volumes.ExportOverride = new(output.ToArray(), manifest);
            var operation = new AgentBrokerWorkspaceOperationRequest(request.OrganizationId, request.RepositoryId, request.WorkspaceId,
                request.WorkItemId, request.AssignmentRevision, prepared.WorkspaceKey, "publish-once", "inspect", "Offline feature");
            Assert.Equal("Modified", (await broker.ExecuteAsync(operation, "http://localhost")).Status);
            var acquired = await broker.LocksAsync(new(operation, "create", "asset.bin"));
            var fileLock = Assert.Single(acquired.Locks);
            Assert.Equal("Locked", acquired.Status); Assert.True(fileLock.OwnedByCaller);
            Assert.Equal(fileLock.Id, Assert.Single((await broker.LocksAsync(new(operation, "create", "asset.bin"))).Locks).Id);
            Assert.Equal(fileLock.Id, Assert.Single((await broker.LocksAsync(new(operation, "list"))).Locks).Id);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => broker.LocksAsync(new(operation with { AssignmentRevision = operation.AssignmentRevision + 1 }, "list")));
            var foreign = await store.LocksAsync(new(request.OrganizationId, request.RepositoryId, Guid.NewGuid(), "Other employee", "create", "README.md"));
            var foreignId = Assert.Single(foreign.Locks).Id;
            Assert.Equal("Denied", (await broker.LocksAsync(new(operation, "unlock", Id: foreignId))).Status);
            Assert.NotEqual("Published", (await broker.ExecuteAsync(operation with { Operation = "publish" }, "http://localhost")).Status);
            await store.LocksAsync(new(request.OrganizationId, request.RepositoryId, foreign.Locks[0].OwnerId, "Other employee", "unlock", Id: foreignId));
            var published = await broker.ExecuteAsync(operation with { Operation = "publish" }, "http://localhost");
            Assert.Equal("Published", published.Status); Assert.Equal("InternalGit", published.Provider);
            Assert.Equal(published.CommitSha, (await broker.ExecuteAsync(operation with { Operation = "publish" }, "http://localhost")).CommitSha);
            var before = await store.ExecuteAsync(new(request.OrganizationId, request.RepositoryId, "inspect"));
            Assert.Equal(prepared.BaseCommitSha, before.Refs.Single(r => r.Name == "refs/heads/main").Sha);
            Assert.Equal("Unlocked", (await broker.LocksAsync(new(operation, "unlock", Id: fileLock.Id))).Status);
            Assert.Equal("Unlocked", (await broker.LocksAsync(new(operation, "unlock", Id: fileLock.Id))).Status);
            var merged = await store.MergeInternalAsync(new(request.OrganizationId, request.RepositoryId, Guid.NewGuid(), workspace.BranchName, "main", published.CommitSha!, "merge-once"));
            Assert.True(merged.Merged);
            var buildRepository = await db.SourceControlRepositories.Include(r => r.Connection).SingleAsync();
            var buildSource = await CSweet.Infrastructure.Setup.ToolchainTrustedSource.PrepareAsync(host, buildRepository,
                new DeliveryBuildRecord { Id = Guid.NewGuid(), OrganizationId = request.OrganizationId,
                    RepositoryId = request.RepositoryId, SourceRevision = merged.MergeCommitSha! }, 1024 * 1024, default);
            var buildDirectory = Path.Combine(root, "build-source"); using var buildArchive = new MemoryStream(buildSource.Archive);
            await artifacts.ExtractZipAsync(buildArchive, buildDirectory);
            Assert.Equal("Offline feature", await File.ReadAllTextAsync(Path.Combine(buildDirectory, "README.md")));
            Assert.Equal(new byte[] { 0, 1, 2, 255 }, await File.ReadAllBytesAsync(Path.Combine(buildDirectory, "asset.bin")));
            var recovered = await store.PrepareAsync(new(request.OrganizationId, request.RepositoryId, Guid.NewGuid(), "main", "work/verify", merged.MergeCommitSha, "verify"), artifacts);
            var restored = Path.Combine(root, "restored"); using var archive = new MemoryStream(recovered.Archive);
            await artifacts.ExtractZipAsync(archive, restored);
            Assert.Equal("Offline feature", await File.ReadAllTextAsync(Path.Combine(restored, "README.md")));
            Assert.Equal(new byte[] { 0, 1, 2, 255 }, await File.ReadAllBytesAsync(Path.Combine(restored, "asset.bin")));
            Assert.False(Directory.Exists(Path.Combine(restored, ".git"))); Assert.Null(host.Request);
            workspace.BaseCommitSha = published.CommitSha!; await db.SaveChangesAsync();
            Assert.True((await broker.ExecuteAsync(operation with { Operation = "cleanup", IdempotencyKey = "cleanup" }, "http://localhost")).Removed);
            Assert.True(volumes.Removed);
        }
        finally
        {
            if (Directory.Exists(root)) { foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal); Directory.Delete(root, true); }
        }
    }

    [Theory]
    [InlineData("policy", false)]
    [InlineData("membership", false)]
    [InlineData("identity", false)]
    [InlineData("team", false)]
    [InlineData("policy", true)]
    [InlineData("membership", true)]
    [InlineData("identity", true)]
    [InlineData("team", true)]
    public async Task PreparationRejectsRevokedTeamAuthorityBeforeFetchingSource(string revoked, bool internalGit)
    {
        await using var db = CreateDb(); var seeded = await SeedAsync(db);
        if (internalGit) (await db.SourceControlConnections.SingleAsync()).Provider = SourceControlProvider.InternalGit;
        if (revoked == "policy") (await db.TeamRepositoryPolicies.SingleAsync()).DisabledAt = DateTimeOffset.UtcNow;
        if (revoked == "membership") (await db.TeamMemberships.SingleAsync()).EndedAt = DateTimeOffset.UtcNow;
        if (revoked == "identity") (await db.CoreOrganizationUsers.SingleAsync()).IsActive = false;
        if (revoked == "team") (await db.OrganizationTeams.SingleAsync()).ArchivedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var host = new FakeHost(); var volumes = new FakeVolumes();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new AgentWorkspaceBroker(db, host, volumes).PrepareAsync(seeded.Request));
        Assert.Null(host.Request); Assert.Null(volumes.Lease);
    }

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
        Assert.Equal(42, host.Request.ExternalRepositoryId);
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
    public async Task GitHubOperationsUsePersistedProviderIdentity(string operation)
    {
        await using var db = CreateDb();
        var request = await SeedOperationAsync(db, operation);
        (await db.SourceControlConnections.SingleAsync()).Provider = SourceControlProvider.GitHub;
        (await db.SourceControlRepositories.SingleAsync()).ExternalRepositoryId = "42";
        await db.SaveChangesAsync();
        var host = new FakeHost(); var volumes = new FakeVolumes();
        var result = await new AgentWorkspaceBroker(db, host, volumes).ExecuteAsync(request with { ProposedChangeTitle = "Feature", ProposedChangeBody = "Details" }, "http://localhost");
        Assert.Null(host.Operation); Assert.Equal(71234, host.GitHubOperation!.InstallationId);
        Assert.Equal(42, host.GitHubOperation.ExternalRepositoryId);
        Assert.Equal("private-owner", host.GitHubOperation.Owner);
        Assert.Equal("csweet/safe-work", host.GitHubOperation.Workspace.Branch);
        Assert.Equal("Feature", host.GitHubOperation.ProposedChangeTitle);
        if (operation == "publish") { Assert.Equal("GitHub", result.Provider); Assert.Equal("https://github.com/private-owner/private-repository/pull/7", result.ReviewUrl); }
        if (operation == "cleanup") { Assert.Equal("Retained", result.Status); Assert.False(volumes.Removed); }
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
            ExternalRepositoryId = "42",
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
        var employee = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = workspace.OrganizationId,
            AgentInstallationId = workspace.AgentInstallationId, IsActive = true, DisplayName = "Developer" };
        db.CoreOrganizationUsers.Add(employee);
        db.OrganizationTeams.Add(new() { Id = workspace.TeamId, OrganizationId = workspace.OrganizationId, Name = "Development" });
        db.TeamMemberships.Add(new() { Id = Guid.NewGuid(), OrganizationId = workspace.OrganizationId, TeamId = workspace.TeamId, OrganizationUserId = employee.Id });
        db.TeamRepositoryPolicies.Add(new() { Id = Guid.NewGuid(), OrganizationId = workspace.OrganizationId, TeamId = workspace.TeamId, RepositoryId = workspace.RepositoryId });
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
        public InternalGitRepositoryStore? NativeStore { get; set; }
        public Task<InternalGitLockResult> InternalLocksAsync(InternalGitLockRequest request, CancellationToken cancellationToken = default) => NativeStore!.LocksAsync(request, cancellationToken);
        public async Task<TrustedWorkspaceSnapshot> PrepareInternalWorkspaceAsync(InternalGitWorkspaceRequest request, CancellationToken cancellationToken = default)
        {
            var snapshot = await NativeStore!.PrepareAsync(request, new WorkspaceArtifactValidator(), cancellationToken);
            return new(snapshot.WorkspaceKey, snapshot.BaseCommitSha, snapshot.Resumed, snapshot.Archive, snapshot.Manifest.Sha256, snapshot.Manifest.FileCount, snapshot.Manifest.TotalBytes);
        }
        public GitHubSnapshotOperation? GitHubOperation { get; private set; }
        public Task<GitHubSnapshotResult> ApplyGitHubSnapshotAsync(GitHubSnapshotOperation request, CancellationToken cancellationToken = default)
        {
            GitHubOperation = request;
            return Task.FromResult(new GitHubSnapshotResult(new(request.Workspace.Operation == "publish" ? "Published" : "Modified", request.Workspace.BaseSha,
                new string('b', 40), ["README.md"], "1 file changed", null), "https://github.com/private-owner/private-repository/pull/7"));
        }
        public InternalGitSnapshotOperation? Operation { get; private set; }
        public bool CleanAtLatest { get; set; }
        public string? LatestSha { get; set; }
        public Task<InternalGitSnapshotResult> ApplyInternalSnapshotAsync(InternalGitSnapshotOperation request, CancellationToken cancellationToken = default)
        {
            Operation = request;
            if (NativeStore is not null) return NativeStore.ApplySnapshotAsync(request, new WorkspaceArtifactValidator(), cancellationToken);
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
        public WorkspaceVolumeExport? ExportOverride { get; set; }
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
        { if (Missing) throw new WorkspaceSnapshotUnavailableException(); Lease = lease; return Task.FromResult(ExportOverride ?? new WorkspaceVolumeExport([1, 2, 3, 4], new(new string('b', 64), 1, 4))); }
    }
}
