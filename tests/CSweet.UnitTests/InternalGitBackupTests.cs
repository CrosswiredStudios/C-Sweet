using System.IO.Compression;
using CSweet.Contracts.SourceControl;
using CSweet.TrustedServices;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class InternalGitBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csweet-backup-tests", Guid.NewGuid().ToString("N"));
    private readonly Guid _business = Guid.NewGuid(), _repository = Guid.NewGuid();
    private readonly InternalGitStorageOptions _options;
    private readonly InternalGitRepositoryStore _store;
    private readonly WorkspaceArtifactValidator _artifacts = new();
    public InternalGitBackupTests()
    {
        var repositories = Path.Combine(_root, "repositories"); Directory.CreateDirectory(repositories);
        File.WriteAllText(Path.Combine(repositories, ".csweet-git-store"), "test");
        _options = new() { RepositoryRoot = repositories, ExpectedStoreId = "test", TemporaryRoot = Path.Combine(_root, "temporary") };
        _store = new(Options.Create(_options));
    }
    private async Task<string> InitializeAsync()
    {
        await _store.ExecuteAsync(new(_business, _repository, "create", "main"));
        return (await _store.PrepareAsync(new(_business, _repository, Guid.NewGuid(), "main", "work", null, "prepare"), _artifacts)).BaseCommitSha;
    }
    private async Task<string> PublishAsync(string sha, string version, string key)
    {
        var directory = Path.Combine(_root, key); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, ".gitattributes"), "*.bin filter=lfs diff=lfs merge=lfs -text\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "asset.bin"), version);
        using var archive = new MemoryStream(); var manifest = await _artifacts.CreateZipAsync(directory, archive);
        return (await _store.ApplySnapshotAsync(new(_business, _repository, _repository, "publish", sha, "work", "main", key,
            archive.ToArray(), manifest.Sha256, manifest.FileCount, manifest.TotalBytes, "Update asset"), _artifacts)).CommitSha!;
    }
    private string BackupPath(Guid backup) => Path.Combine(_root, "backups", _options.Backup.KeyPrefix, _business.ToString("N"), _repository.ToString("N"), backup.ToString("N"), "archive.zip");

    [Fact]
    public async Task BackupRestoresExactRefsAndHistoricalLfsWithoutTheSourceRepository()
    {
        var initial = await InitializeAsync(); var first = await PublishAsync(initial, "asset version one", "first");
        var second = await PublishAsync(first, "asset version two", "second");
        await _store.ExecuteAsync(new(_business, _repository, "update-ref", Ref: "refs/tags/v1", ExpectedSha: new string('0', 40), TargetSha: first));
        var sourcePath = Path.Combine(_options.RepositoryRoot, _business.ToString("N"), _repository.ToString("N") + ".git");
        await File.WriteAllTextAsync(Path.Combine(sourcePath, "private-note"), "not-a-real-credential");
        var lockOwner = Guid.NewGuid();
        await _store.LocksAsync(new(_business, _repository, lockOwner, "Original owner", "create", "asset.bin"));
        var backupId = Guid.NewGuid(); var request = new InternalGitBackupRequest(_business, _repository, backupId);
        var backup = await _store.CreateBackupAsync(request);
        Assert.Equal(2, backup.LfsObjectCount); Assert.Equal(backup, await _store.CreateBackupAsync(request));
        Assert.Single(await _store.ListBackupsAsync(_business)); Assert.Empty(await _store.ListBackupsAsync(Guid.NewGuid()));
        using (var zip = ZipFile.OpenRead(BackupPath(backupId))) Assert.All(zip.Entries, e => Assert.True(e.FullName == "repository.bundle" || e.FullName.StartsWith("lfs/")));
        await _store.ExecuteAsync(new(_business, _repository, "delete"));
        var restoredId = Guid.NewGuid(); var restore = new InternalGitBackupRestoreRequest(_business, _repository, backupId, restoredId);
        await _store.RestoreBackupAsync(restore); await _store.RestoreBackupAsync(restore);
        Assert.Empty((await _store.LocksAsync(new(_business, restoredId, lockOwner, "Original owner", "list"))).Locks);
        var restored = await _store.ExecuteAsync(new(_business, restoredId, "inspect"));
        Assert.Contains(restored.Refs, r => r.Name == "refs/tags/v1" && r.Sha == first);
        Assert.Contains(restored.Refs, r => r.Name == "refs/heads/work" && r.Sha == second);
        foreach (var revision in new[] { (first, "asset version one"), (second, "asset version two") })
        {
            var snapshot = await _store.PrepareAsync(new(_business, restoredId, Guid.NewGuid(), "main", "work", revision.Item1, "prepare-restored"), _artifacts);
            var output = Path.Combine(_root, Guid.NewGuid().ToString("N")); await _artifacts.ExtractZipAsync(new MemoryStream(snapshot.Archive), output);
            Assert.Equal(revision.Item2, await File.ReadAllTextAsync(Path.Combine(output, "asset.bin")));
        }
        Assert.False(File.Exists(Path.Combine(_options.RepositoryRoot, _business.ToString("N"), restoredId.ToString("N") + ".git", "private-note")));
        await _store.DeleteBackupAsync(request); await _store.DeleteBackupAsync(request); Assert.Empty(await _store.ListBackupsAsync(_business));
    }
    [Fact]
    public async Task RestoreRejectsCorruptArchiveAndNeverOverwritesExistingRepository()
    {
        await InitializeAsync(); var backupId = Guid.NewGuid(); await _store.CreateBackupAsync(new(_business, _repository, backupId));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.RestoreBackupAsync(new(_business, _repository, backupId, _repository)));
        var existing = Guid.NewGuid(); await _store.ExecuteAsync(new(_business, existing, "create", "trunk"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.RestoreBackupAsync(new(_business, _repository, backupId, existing)));
        Assert.Equal("trunk", (await _store.ExecuteAsync(new(_business, existing, "inspect"))).DefaultBranch);
        var bytes = await File.ReadAllBytesAsync(BackupPath(backupId)); bytes[bytes.Length / 2] ^= 1; await File.WriteAllBytesAsync(BackupPath(backupId), bytes);
        var target = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.RestoreBackupAsync(new(_business, _repository, backupId, target)));
        Assert.False(Directory.Exists(Path.Combine(_options.RepositoryRoot, _business.ToString("N"), target.ToString("N") + ".git")));
    }
    [Fact]
    public async Task EmptyRepositoryBackupPreservesItsInitialBranch()
    {
        await _store.ExecuteAsync(new(_business, _repository, "create", "trunk"));
        var backup = await _store.CreateBackupAsync(new(_business, _repository, Guid.NewGuid()));
        Assert.Equal(0, backup.RefCount); var target = Guid.NewGuid();
        await _store.RestoreBackupAsync(new(_business, _repository, backup.Id, target));
        var restored = await _store.ExecuteAsync(new(_business, target, "inspect")); Assert.Equal("trunk", restored.DefaultBranch); Assert.Empty(restored.Refs);
    }
    [Fact]
    public async Task UnavailableBackupMountAndSizeLimitNeverPublishCompletedBackup()
    {
        await InitializeAsync(); _options.Backup.RootPath = Path.Combine(_root, "unmounted"); _options.Backup.ExpectedStoreId = "nas";
        await Assert.ThrowsAsync<IOException>(() => _store.CreateBackupAsync(new(_business, _repository, Guid.NewGuid())));
        Assert.False(Directory.Exists(_options.Backup.RootPath));
        _options.Backup.RootPath = null; _options.Backup.ExpectedStoreId = null; _options.Backup.MaximumObjectBytes = 64;
        await Assert.ThrowsAsync<IOException>(() => _store.CreateBackupAsync(new(_business, _repository, Guid.NewGuid())));
        Assert.Empty(await _store.ListBackupsAsync(_business));
    }
    [Fact]
    public async Task SeparateBackupMountCanBeListedWithoutLiveRepositoryStorage()
    {
        await InitializeAsync(); _options.Backup.RootPath = Path.Combine(_root, "backup-nas"); _options.Backup.ExpectedStoreId = "backup-nas";
        Directory.CreateDirectory(_options.Backup.RootPath); await File.WriteAllTextAsync(Path.Combine(_options.Backup.RootPath, ".csweet-object-store"), "backup-nas");
        await _store.CreateBackupAsync(new(_business, _repository, Guid.NewGuid()));
        await File.WriteAllTextAsync(Path.Combine(_options.RepositoryRoot, ".csweet-git-store"), "wrong-mount");
        Assert.Single(await _store.ListBackupsAsync(_business));
    }
    [Fact]
    public async Task MissingHistoricalLfsDoesNotPublishCompletedBackup()
    {
        var initial = await InitializeAsync(); await PublishAsync(initial, "asset content", "missing-lfs");
        var lfsRoot = Path.Combine(_root, "lfs");
        foreach (var file in Directory.EnumerateFiles(lfsRoot, "*", SearchOption.AllDirectories)) File.Delete(file);
        await Assert.ThrowsAnyAsync<IOException>(() => _store.CreateBackupAsync(new(_business, _repository, Guid.NewGuid())));
        Assert.Empty(await _store.ListBackupsAsync(_business));
    }

    public void Dispose()
    {
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "csweet-backup-tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(_root).StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid test cleanup path.");
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
}
