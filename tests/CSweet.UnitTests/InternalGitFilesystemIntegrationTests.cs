using CSweet.Contracts.SourceControl;
using CSweet.TrustedServices;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class FilesystemIntegrationFactAttribute : FactAttribute
{
    public FilesystemIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_TEST_STORAGE_PARENT")))
            Skip = "Run scripts/Test-InternalGitFilesystem.ps1 with an existing test parent directory.";
    }
}

public sealed class InternalGitFilesystemIntegrationTests
{
    [FilesystemIntegrationFact]
    public async Task MountedStorageSupportsPublicationExclusiveAccessAndBackupRecovery()
    {
        var parent = Environment.GetEnvironmentVariable("CSWEET_TEST_STORAGE_PARENT")!;
        if (!Path.IsPathFullyQualified(parent) || !Directory.Exists(parent)) throw new ArgumentException("The storage test parent must already exist and be absolute.");
        var identity = "csweet-storage-test-" + Guid.NewGuid().ToString("N");
        var root = Path.GetFullPath(Path.Combine(parent, identity));
        var prefix = Path.GetFullPath(parent);
        if (!Path.EndsInDirectorySeparator(prefix)) prefix += Path.DirectorySeparatorChar;
        if (!root.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("Test directory escapes its parent.");
        Directory.CreateDirectory(root);
        var ownership = Path.Combine(root, ".csweet-test-owner"); await File.WriteAllTextAsync(ownership, identity);
        try
        {
            var repositories = Path.Combine(root, "repositories"); var lfsRoot = Path.Combine(root, "lfs"); var backups = Path.Combine(root, "backups");
            foreach (var directory in new[] { repositories, lfsRoot, backups }) Directory.CreateDirectory(directory);
            var marker = Path.Combine(repositories, ".csweet-git-store"); await File.WriteAllTextAsync(marker, identity);
            await File.WriteAllTextAsync(Path.Combine(lfsRoot, ".csweet-object-store"), identity);
            await File.WriteAllTextAsync(Path.Combine(backups, ".csweet-object-store"), identity);
            var options = new InternalGitStorageOptions { RepositoryRoot = repositories, ExpectedStoreId = identity, TemporaryRoot = Path.Combine(root, "operations"),
                Lfs = new() { RootPath = lfsRoot, ExpectedStoreId = identity }, Backup = new() { RootPath = backups, ExpectedStoreId = identity } };
            var store = new InternalGitRepositoryStore(Options.Create(options));
            Assert.True((await store.StatusAsync()).Ready); // Flush and atomic rename probe on the selected filesystem.
            var business = Guid.NewGuid(); var repository = Guid.NewGuid(); var workspace = Guid.NewGuid();
            var artifacts = new WorkspaceArtifactValidator(); await store.ExecuteAsync(new(business, repository, "create", "main"));
            var initial = await store.PrepareAsync(new(business, repository, workspace, "main", "work", null, "prepare"), artifacts);
            var input = Path.Combine(root, "input"); Directory.CreateDirectory(input);
            var asset = new byte[1024 * 1024]; Random.Shared.NextBytes(asset);
            await File.WriteAllTextAsync(Path.Combine(input, ".gitattributes"), "*.bin filter=lfs diff=lfs merge=lfs -text\n");
            await File.WriteAllBytesAsync(Path.Combine(input, "asset.bin"), asset);
            using var archive = new MemoryStream(); var manifest = await artifacts.CreateZipAsync(input, archive);
            var publication = new InternalGitSnapshotOperation(business, repository, workspace, "publish", initial.BaseCommitSha, "work", "main", "publish",
                archive.ToArray(), manifest.Sha256, manifest.FileCount, manifest.TotalBytes, "Storage verification");
            var lockPath = Path.Combine(repositories, business.ToString("N"), repository.ToString("N") + ".git.lock");
            await using (var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                await Assert.ThrowsAsync<IOException>(() => new InternalGitRepositoryStore(Options.Create(options)).ApplySnapshotAsync(publication, artifacts));
            Assert.DoesNotContain((await store.ExecuteAsync(new(business, repository, "inspect"))).Refs, r => r.Name == "refs/heads/work");
            var published = await store.ApplySnapshotAsync(publication, artifacts);
            var backupRequest = new InternalGitBackupRequest(business, repository, Guid.NewGuid()); await store.CreateBackupAsync(backupRequest);
            await store.ExecuteAsync(new(business, repository, "delete"));
            // Remove only this fixture's original LFS objects so recovery must use its backup.
            var originalLfs = Path.GetFullPath(Path.Combine(lfsRoot, options.Lfs.KeyPrefix, business.ToString("N"), repository.ToString("N")));
            Assert.StartsWith(Path.GetFullPath(lfsRoot) + Path.DirectorySeparatorChar, originalLfs);
            Assert.True(Directory.Exists(originalLfs)); Directory.Delete(originalLfs, true);
            var restored = Guid.NewGuid(); var restore = new InternalGitBackupRestoreRequest(business, repository, backupRequest.BackupId, restored);
            await store.RestoreBackupAsync(restore); await store.RestoreBackupAsync(restore);
            var snapshot = await store.PrepareAsync(new(business, restored, Guid.NewGuid(), "main", "work", published.CommitSha, "verify"), artifacts);
            using var source = new MemoryStream(snapshot.Archive); var output = Path.Combine(root, "verified"); await artifacts.ExtractZipAsync(source, output);
            Assert.Equal(asset, await File.ReadAllBytesAsync(Path.Combine(output, "asset.bin")));
            Assert.Equal(published.CommitSha, (await store.ExecuteAsync(new(business, restored, "inspect"))).Refs.Single(r => r.Name == "refs/heads/work").Sha);
            await File.WriteAllTextAsync(marker, "wrong-store"); Assert.False((await store.StatusAsync()).Ready);
            var rejected = Guid.NewGuid(); await Assert.ThrowsAsync<IOException>(() => store.ExecuteAsync(new(business, rejected, "create", "main")));
            Assert.False(Directory.Exists(Path.Combine(repositories, business.ToString("N"), rejected.ToString("N") + ".git")));
            await File.WriteAllTextAsync(marker, identity); await store.DeleteBackupAsync(backupRequest); Assert.Empty(await store.ListBackupsAsync(business));
        }
        finally
        {
            // Never remove the supplied parent or an unowned/replaced test directory.
            if (!Directory.Exists(root) || new DirectoryInfo(root).LinkTarget is not null || !File.Exists(ownership) || await File.ReadAllTextAsync(ownership) != identity)
                throw new IOException("Test directory identity changed; automatic cleanup refused.");
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }
}
