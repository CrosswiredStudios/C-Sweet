using CSweet.Contracts.SourceControl;
using CSweet.TrustedServices;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class InternalGitPublicationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csweet-publication-tests", Guid.NewGuid().ToString("N"));
    private readonly Guid _business = Guid.NewGuid(), _repository = Guid.NewGuid(), _workspace = Guid.NewGuid();
    private readonly WorkspaceArtifactValidator _artifacts = new();
    private readonly InternalGitRepositoryStore _store;
    public InternalGitPublicationTests()
    {
        var repositories = Path.Combine(_root, "repositories");
        Directory.CreateDirectory(repositories);
        File.WriteAllText(Path.Combine(repositories, ".csweet-git-store"), "test");
        _store = new(Options.Create(new InternalGitStorageOptions
        {
            RepositoryRoot = repositories, ExpectedStoreId = "test", TemporaryRoot = Path.Combine(_root, "temp")
        }));
    }

    private async Task<string> InitializeAsync()
    {
        await _store.ExecuteAsync(new(_business, _repository, "create", "main"));
        return (await _store.PrepareAsync(new(_business, _repository, _workspace, "main", "work/one", null, "prepare"), _artifacts)).BaseCommitSha;
    }

    private async Task<InternalGitSnapshotOperation> RequestAsync(string baseSha, string operation, string branch, string key,
        params (string Name, string Content)[] files)
    {
        var path = Path.Combine(_root, "inputs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        foreach (var file in files)
        {
            var target = Path.Combine(path, file.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, file.Content);
        }
        using var output = new MemoryStream();
        var manifest = await _artifacts.CreateZipAsync(path, output);
        return new(_business, _repository, _workspace, operation, baseSha, branch, "main", key,
            output.ToArray(), manifest.Sha256, manifest.FileCount, manifest.TotalBytes, "Implement feature");
    }

    [Fact]
    public async Task PublishesExactContentWithoutChangingMainAndReplaysIdempotently()
    {
        var initial = await InitializeAsync();
        var request = await RequestAsync(initial, "publish", "work/one", "publish-1", ("hello.txt", "hello world"));
        var result = await _store.ApplySnapshotAsync(request, _artifacts);
        Assert.Equal("Published", result.Status);
        Assert.Contains("hello.txt", result.ChangedFiles);
        Assert.Equal(initial, (await _store.ExecuteAsync(new(_business, _repository, "inspect"))).Commits[0].Sha);
        var branch = await _store.ExecuteAsync(new(_business, _repository, "inspect", Ref: "refs/heads/work/one", Path: "hello.txt"));
        Assert.Equal("hello world", branch.Content);
        var diff = await _store.ExecuteAsync(new(_business, _repository, "compare", Name: "main", ExpectedSha: result.CommitSha));
        Assert.Contains("hello.txt", diff.Files); Assert.Contains("+hello world", diff.Content);
        Assert.Equal(result.CommitSha, (await _store.ApplySnapshotAsync(request, _artifacts)).CommitSha);
        var clean = await _store.ApplySnapshotAsync(request with { Operation = "inspect", BaseSha = result.CommitSha! }, _artifacts);
        Assert.Empty(clean.ChangedFiles);
        Assert.Equal("Clean", clean.Status);
        var different = await RequestAsync(initial, "publish", "work/one", "publish-1", ("hello.txt", "different"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.ApplySnapshotAsync(different, _artifacts));
    }

    [Fact]
    public async Task RejectsDirectMainWritesAndStaleWorkBranchUpdates()
    {
        var initial = await InitializeAsync();
        var request = await RequestAsync(initial, "publish", "main", "publish-1", ("file", "one"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _store.ApplySnapshotAsync(request, _artifacts));
        request = request with { Branch = "work/one" };
        await _store.ApplySnapshotAsync(request, _artifacts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.ApplySnapshotAsync(request with { IdempotencyKey = "publish-2" }, _artifacts));
    }

    [Fact]
    public async Task MergeVerifiesHeadAndUsesDurableReceipt()
    {
        var initial = await InitializeAsync();
        var publication = await _store.ApplySnapshotAsync(await RequestAsync(initial, "publish", "work/one", "publish-1", ("file", "value")), _artifacts);
        var request = new InternalGitMergeRequest(_business, _repository, Guid.NewGuid(), "work/one", "main", publication.CommitSha!, "merge-1");
        var rejected = await _store.MergeInternalAsync(request with { ExpectedHeadSha = initial });
        Assert.False(rejected.HeadMatched);
        var merged = await _store.MergeInternalAsync(request);
        Assert.True(merged.Merged);
        var historicalDiff = await _store.ExecuteAsync(new(_business, _repository, "compare", Name: "main", ExpectedSha: publication.CommitSha, TargetSha: merged.MergeCommitSha));
        Assert.Contains("+value", historicalDiff.Content);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.MergeInternalAsync(request with { TargetBranch = "other" }));
        Assert.Equal(merged.MergeCommitSha, (await _store.MergeInternalAsync(request)).MergeCommitSha);
        Assert.Equal("value", (await _store.ExecuteAsync(new(_business, _repository, "inspect", Path: "file"))).Content);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.MergeInternalAsync(request with { ExpectedHeadSha = initial }));
    }

    [Fact]
    public async Task ConflictingMergeDoesNotChangeTarget()
    {
        var initial = await InitializeAsync();
        var first = await _store.ApplySnapshotAsync(await RequestAsync(initial, "publish", "work/one", "publish-1", ("file", "one")), _artifacts);
        var second = await _store.ApplySnapshotAsync(await RequestAsync(initial, "publish", "work/two", "publish-2", ("file", "two")), _artifacts);
        var merged = await _store.MergeInternalAsync(new(_business, _repository, Guid.NewGuid(), "work/one", "main", first.CommitSha!, "merge-1"));
        var conflict = await _store.MergeInternalAsync(new(_business, _repository, Guid.NewGuid(), "work/two", "main", second.CommitSha!, "merge-2"));
        Assert.False(conflict.Merged);
        Assert.True(conflict.HeadMatched);
        Assert.Equal("merge_conflict", conflict.FailureCode);
        Assert.Equal(merged.MergeCommitSha, (await _store.ExecuteAsync(new(_business, _repository, "inspect"))).Commits[0].Sha);
    }

    [Fact]
    public async Task LfsPublicationStoresPointerAndPreparationRestoresAsset()
    {
        var initial = await InitializeAsync();
        var request = await RequestAsync(initial, "publish", "work/assets", "publish-assets",
            (".gitattributes", "*.bin filter=lfs diff=lfs merge=lfs -text\n"), ("texture.bin", "asset payload"));
        var publication = await _store.ApplySnapshotAsync(request, _artifacts);
        var pointer = await _store.ExecuteAsync(new(_business, _repository, "inspect", Ref: "refs/heads/work/assets", Path: "texture.bin"));
        Assert.StartsWith("version https://git-lfs.github.com/spec/v1\n", pointer.Content);
        var snapshot = await _store.PrepareAsync(new(_business, _repository, _workspace, "main", "work/assets", publication.CommitSha, "prepare-assets"), _artifacts);
        var target = Path.Combine(_root, "asset-output");
        await _artifacts.ExtractZipAsync(new MemoryStream(snapshot.Archive), target);
        Assert.Equal("asset payload", await File.ReadAllTextAsync(Path.Combine(target, "texture.bin")));
        Assert.Empty((await _store.ApplySnapshotAsync(request with { BaseSha = publication.CommitSha!, Operation = "inspect" }, _artifacts)).ChangedFiles);
    }

    [Fact]
    public async Task WorkspacePreparationPreservesTrackedFilesDespiteReleaseArchiveAttributes()
    {
        var initial = await InitializeAsync();
        var request = await RequestAsync(initial, "publish", "work/one", "publish-1",
            (".gitattributes", "hidden.txt export-ignore\nversion.txt export-subst\nfolder export-ignore\n"),
            ("hidden.txt", "tracked content"), ("version.txt", "$Format:%H$"), ("folder/inside.txt", "nested content"));
        var published = await _store.ApplySnapshotAsync(request, _artifacts);
        var snapshot = await _store.PrepareAsync(new(_business, _repository, _workspace, "main", "work/one", published.CommitSha, "prepare-complete"), _artifacts);
        var output = Path.Combine(_root, "complete-output");
        await _artifacts.ExtractZipAsync(new MemoryStream(snapshot.Archive), output);
        Assert.Equal("tracked content", await File.ReadAllTextAsync(Path.Combine(output, "hidden.txt")));
        Assert.Equal("nested content", await File.ReadAllTextAsync(Path.Combine(output, "folder", "inside.txt")));
        Assert.Equal("$Format:%H$", await File.ReadAllTextAsync(Path.Combine(output, "version.txt")));
        Assert.Empty((await _store.ApplySnapshotAsync(request with { Operation = "inspect", BaseSha = published.CommitSha! }, _artifacts)).ChangedFiles);
    }

    [Fact]
    public async Task RejectsUnverifiedSnapshotBeforeUpdatingRefs()
    {
        var initial = await InitializeAsync();
        var request = await RequestAsync(initial, "publish", "work/one", "publish-1", ("file", "value"));
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.ApplySnapshotAsync(request with { ArchiveManifestSha = new string('0', 64) }, _artifacts));
        Assert.Single((await _store.ExecuteAsync(new(_business, _repository, "inspect"))).Refs);
    }

    public void Dispose()
    {
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "csweet-publication-tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(_root).StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
}
