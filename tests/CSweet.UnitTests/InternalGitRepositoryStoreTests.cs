using CSweet.Contracts.SourceControl;
using CSweet.TrustedServices;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class InternalGitRepositoryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csweet-internal-git-tests", Guid.NewGuid().ToString("N"));
    private readonly Guid _business = Guid.NewGuid();
    private readonly Guid _repository = Guid.NewGuid();
    private readonly InternalGitStorageOptions _options;
    private InternalGitRepositoryStore Store => new(Options.Create(_options));

    public InternalGitRepositoryStoreTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".csweet-git-store"), "test-store");
        _options = new() { RepositoryRoot = _root, ExpectedStoreId = "test-store" };
    }

    [Fact]
    public async Task CreatesEmptyBareRepositoryAndCanChangeInitialBranch()
    {
        var created = await Store.ExecuteAsync(new(_business, _repository, "create", "trunk"));
        Assert.Equal("trunk", created.DefaultBranch);
        Assert.Empty(created.Refs);
        Assert.True(File.Exists(Path.Combine(_root, _business.ToString("N"), _repository.ToString("N") + ".git", "HEAD")));
        var changed = await Store.ExecuteAsync(new(_business, _repository, "default-branch", "development"));
        Assert.Equal("development", changed.DefaultBranch);
        var replay = await Store.ExecuteAsync(new(_business, _repository, "create", "trunk"));
        Assert.Equal("development", replay.DefaultBranch);
    }

    [Fact]
    public async Task EmptyRepositoryProducesCredentialFreeExactWorkspaceAndHistory()
    {
        await Store.ExecuteAsync(new(_business, _repository, "create", "main"));
        var snapshot = await Store.PrepareAsync(new(_business, _repository, Guid.NewGuid(), "main", "work/one", null, "prepare-1"), new());
        Assert.Equal(40, snapshot.BaseCommitSha.Length);
        Assert.Equal(0, snapshot.Manifest.FileCount);
        var inspection = await Store.ExecuteAsync(new(_business, _repository, "inspect"));
        Assert.Single(inspection.Commits);
        Assert.Equal(snapshot.BaseCommitSha, inspection.Commits[0].Sha);
        var exact = await Store.PrepareAsync(new(_business, _repository, Guid.NewGuid(), "main", "work/two", snapshot.BaseCommitSha, "prepare-2"), new());
        Assert.Equal(snapshot.BaseCommitSha, exact.BaseCommitSha);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.PrepareAsync(
            new(_business, _repository, Guid.NewGuid(), "main", "work/three", new string('f', 40), "prepare-3"), new()));
    }

    [Fact]
    public async Task RefMutationsRequireExactOldValueAndProtectDefaultBranch()
    {
        await Store.ExecuteAsync(new(_business, _repository, "create", "main"));
        var snapshot = await Store.PrepareAsync(new(_business, _repository, Guid.NewGuid(), "main", "work/one", null, "prepare-1"), new());
        var branch = await Store.ExecuteAsync(new(_business, _repository, "update-ref", Ref: "refs/heads/feature",
            ExpectedSha: new string('0', 40), TargetSha: snapshot.BaseCommitSha));
        Assert.Equal(2, branch.Refs.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.ExecuteAsync(new(_business, _repository, "update-ref",
            Ref: "refs/heads/feature", ExpectedSha: new string('0', 40), TargetSha: snapshot.BaseCommitSha)));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.ExecuteAsync(new(_business, _repository, "delete-ref",
            Ref: "refs/heads/main", ExpectedSha: snapshot.BaseCommitSha)));
        var deleted = await Store.ExecuteAsync(new(_business, _repository, "delete-ref", Ref: "refs/heads/feature", ExpectedSha: snapshot.BaseCommitSha));
        Assert.Single(deleted.Refs);
    }

    [Fact]
    public async Task DeleteRemovesOnlySelectedRepositoryAndIsIdempotent()
    {
        await Store.ExecuteAsync(new(_business, _repository, "create", "main"));
        var other = Guid.NewGuid();
        await Store.ExecuteAsync(new(_business, other, "create", "main"));
        await Store.PrepareAsync(new(_business, _repository, Guid.NewGuid(), "main", "work/one", null, "prepare"), new());
        await Store.ExecuteAsync(new(_business, _repository, "delete"));
        await Store.ExecuteAsync(new(_business, _repository, "delete"));
        Assert.Equal("main", (await Store.ExecuteAsync(new(_business, other, "inspect"))).DefaultBranch);
        Assert.True(File.Exists(Path.Combine(_root, ".csweet-git-store")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Store.ExecuteAsync(new(_business, _repository, "inspect")));
    }

    [Fact]
    public async Task MissingOrMismatchedNasMarkerFailsWithoutCreatingStorage()
    {
        File.Delete(Path.Combine(_root, ".csweet-git-store"));
        var status = await Store.StatusAsync();
        Assert.False(status.Ready);
        await Assert.ThrowsAsync<IOException>(() => Store.ExecuteAsync(new(_business, _repository, "create", "main")));
        Assert.False(Directory.Exists(Path.Combine(_root, _business.ToString("N"))));
        File.WriteAllText(Path.Combine(_root, ".csweet-git-store"), "another-store");
        Assert.False((await Store.StatusAsync()).Ready);
    }

    [Fact]
    public async Task CustomRootNeverFallsBackToHostStorage()
    {
        _options.ExpectedStoreId = null;
        Assert.False((await Store.StatusAsync()).Ready);
    }

    [Theory]
    [InlineData("refs/heads/../escape")]
    [InlineData("refs/heads/-bad.lock")]
    [InlineData("refs/heads/main:other")]
    [InlineData("refs/heads/a b")]
    [InlineData("refs/heads/a@{b")]
    [InlineData("refs/remotes/main")]
    public void RejectsUnsafeRefs(string reference) => Assert.Throws<ArgumentException>(() => InternalGitRepositoryStore.ValidateRef(reference));

    [Fact]
    public async Task DoesNotResolveRepositoryFromAnotherBusiness()
    {
        await Store.ExecuteAsync(new(_business, _repository, "create", "main"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Store.ExecuteAsync(new(Guid.NewGuid(), _repository, "inspect")));
    }

    [Fact]
    public void MinioConfigurationRequiresSecureEndpointAndPairedCredentials()
    {
        var options = new InternalGitObjectStorageOptions { Provider = "s3", BucketName = "git-lfs", ServiceUrl = "https://nas.example:9000" };
        options.Validate();
        options.ServiceUrl = "http://nas.example:9000";
        Assert.Throws<ArgumentException>(options.Validate);
        options.ServiceUrl = "http://localhost:9000";
        options.AccessKeyId = "key";
        Assert.Throws<ArgumentException>(options.Validate);
    }

    public void Dispose()
    {
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "csweet-internal-git-tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(_root).StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
    }
}
