using CSweet.Application.SourceControl;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;

namespace CSweet.UnitTests;

public sealed class ToolchainTrustedSourceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExactBuildSourceUsesPersistedProviderIdentity(bool internalGit)
    {
        var (repository, build) = Seed(internalGit);
        var host = new Host();
        var result = await ToolchainTrustedSource.PrepareAsync(host, repository, build, 100, default);
        Assert.Equal(new byte[] { 1, 2 }, result.Archive);
        if (internalGit)
        {
            Assert.Null(host.GitHub); Assert.NotNull(host.Internal);
            Assert.Equal(repository.Id, host.Internal.RepositoryId);
            Assert.Equal(build.OrganizationId, host.Internal.OrganizationId);
            Assert.Equal(build.SourceRevision, host.Internal.ExpectedSha);
        }
        else
        {
            Assert.Null(host.Internal); Assert.Equal(42, host.GitHub!.ExternalRepositoryId);
            Assert.Equal(build.SourceRevision, host.GitHub.ExpectedCommitSha);
        }
    }

    [Theory]
    [InlineData("archived")]
    [InlineData("disconnected")]
    [InlineData("business")]
    [InlineData("repository")]
    [InlineData("connection-business")]
    [InlineData("provider")]
    public async Task UnavailableOrForeignSourceIsRejectedBeforeHostAccess(string reason)
    {
        var (repository, build) = Seed(true); var host = new Host();
        if (reason == "archived") repository.ArchivedAt = DateTimeOffset.UtcNow;
        if (reason == "disconnected") repository.Connection!.Status = SourceControlConnectionStatus.Disconnected;
        if (reason == "business") build.OrganizationId = Guid.NewGuid();
        if (reason == "repository") build.RepositoryId = Guid.NewGuid();
        if (reason == "connection-business") repository.Connection!.OrganizationId = Guid.NewGuid();
        if (reason == "provider") repository.Connection!.Provider = SourceControlProvider.GenericGit;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ToolchainTrustedSource.PrepareAsync(host, repository, build, 100, default));
        Assert.Null(host.Internal); Assert.Null(host.GitHub);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WrongRevisionOrOversizedSnapshotIsRejected(bool changed)
    {
        var (repository, build) = Seed(true); var host = new Host { WrongRevision = changed };
        await Assert.ThrowsAsync<InvalidDataException>(() => ToolchainTrustedSource.PrepareAsync(host, repository, build, changed ? 100 : 1, default));
    }

    private static (SourceControlRepository, DeliveryBuildRecord) Seed(bool internalGit)
    {
        var business = Guid.NewGuid();
        var repository = new SourceControlRepository { Id = Guid.NewGuid(), OrganizationId = business, ExternalRepositoryId = "42",
            Owner = "owner", Name = "repo", Status = SourceControlRepositoryStatus.Ready, IsPrivate = true,
            Connection = new() { OrganizationId = business, Provider = internalGit ? SourceControlProvider.InternalGit : SourceControlProvider.GitHub,
                Status = SourceControlConnectionStatus.Connected, SourceAccessInstallationId = internalGit ? null : 12 } };
        return (repository, new() { Id = Guid.NewGuid(), OrganizationId = business, RepositoryId = repository.Id, SourceRevision = new string('a', 40) });
    }

    private sealed class Host : ITrustedSourceControlHostClient
    {
        public InternalGitWorkspaceRequest? Internal;
        public TrustedWorkspaceSnapshotRequest? GitHub;
        public bool WrongRevision;
        private Task<TrustedWorkspaceSnapshot> Snapshot() => Task.FromResult(new TrustedWorkspaceSnapshot("build", new string(WrongRevision ? 'b' : 'a', 40), false, [1, 2], new string('d', 64), 1, 2));
        public Task<TrustedWorkspaceSnapshot> PrepareInternalWorkspaceAsync(InternalGitWorkspaceRequest request, CancellationToken cancellationToken = default) { Internal = request; return Snapshot(); }
        public Task<TrustedWorkspaceSnapshot> PrepareWorkspaceAsync(TrustedWorkspaceSnapshotRequest request, CancellationToken cancellationToken = default) { GitHub = request; return Snapshot(); }
        public Task<TrustedInstallationDescriptor> DescribeInstallationAsync(long installationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TrustedRepositoryDescriptor>> ListRepositoriesAsync(long installationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TrustedMergeResult> MergeAsync(TrustedMergeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
