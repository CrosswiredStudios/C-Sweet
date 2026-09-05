using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CSweet.Contracts.SourceControl;
using Microsoft.Extensions.Options;

namespace CSweet.TrustedServices;

public sealed partial class InternalGitRepositoryStore
{
    public async Task<IReadOnlyList<InternalGitBackupSummary>> ListBackupsAsync(Guid business, CancellationToken ct = default)
    {
        using var storage = new InternalGitBackupStorage(_options);
        return (await storage.ListAsync(business, ct)).Select(BackupSummary).ToList();
    }
    public async Task DeleteBackupAsync(InternalGitBackupRequest request, CancellationToken ct = default)
    {
        using var storage = new InternalGitBackupStorage(_options);
        await storage.DeleteAsync(request.OrganizationId, request.RepositoryId, request.BackupId, ct);
    }
    public async Task<InternalGitBackupSummary> CreateBackupAsync(InternalGitBackupRequest request, CancellationToken ct = default)
    {
        if (request.BackupId == Guid.Empty) throw new ArgumentException("Backup identity is required.");
        var repository = RepositoryPath(request.OrganizationId, request.RepositoryId);
        if (!Directory.Exists(repository)) throw new KeyNotFoundException("Repository does not exist.");
        await using var lease = new FileStream(repository + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var storage = new InternalGitBackupStorage(_options);
        var existing = await storage.ReadAsync(request.OrganizationId, request.RepositoryId, request.BackupId, ct);
        if (existing is not null) return BackupSummary(existing);
        var temporary = NewBackupTemporaryDirectory();
        try
        {
            var refs = (await RunAsync(repository, ["for-each-ref", "--format=%(refname) %(objectname)"], ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim().Split(' ', 2)).Select(p => new InternalGitRef(p[0], p[1])).ToList();
            foreach (var reference in refs) { ValidateBackupRef(reference.Name); ValidateSha(reference.Sha); }
            var branch = (await RunAsync(repository, ["symbolic-ref", "--short", "HEAD"], ct)).Trim(); ValidateBranch(branch);
            await RunAsync(repository, ["fsck", "--full"], ct);
            var assets = refs.Count == 0 ? [] : await FindBackupLfsObjectsAsync(repository, ct);
            var bundle = Path.Combine(temporary, "repository.bundle");
            if (refs.Count > 0) await RunAsync(repository, ["bundle", "create", bundle, "--all"], ct);
            var archivePath = Path.Combine(temporary, "archive.zip");
            await using (var output = new BackupArchiveStream(archivePath, _options.Backup.MaximumObjectBytes))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    if (refs.Count > 0)
                    {
                        await using var entry = zip.CreateEntry("repository.bundle", CompressionLevel.NoCompression).Open();
                        await using var input = File.OpenRead(bundle); await input.CopyToAsync(entry, ct);
                    }
                    using var lfs = new InternalGitLfsStore(Options.Create(_options));
                    foreach (var asset in assets)
                    {
                        await using var entry = zip.CreateEntry("lfs/" + asset.Oid, CompressionLevel.NoCompression).Open();
                        await lfs.CopyToAsync(request.OrganizationId, request.RepositoryId, asset.Oid, entry, ct, asset.Size);
                    }
                }
                output.Flush(true);
            }
            await using var archive = File.OpenRead(archivePath);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(archive, ct)).ToLowerInvariant();
            var manifest = new InternalGitBackupManifest(1, request.OrganizationId, request.RepositoryId, request.BackupId, DateTimeOffset.UtcNow,
                branch, archive.Length, hash, refs, assets);
            await storage.PutAsync(manifest, archivePath, ct);
            return BackupSummary(manifest);
        }
        finally { DeleteOperationDirectory(temporary); }
    }
    public async Task<InternalGitBackupSummary> RestoreBackupAsync(InternalGitBackupRestoreRequest request, CancellationToken ct = default)
    {
        if (request.TargetRepositoryId == request.RepositoryId) throw new ArgumentException("Restore requires a separate repository.");
        var target = RepositoryPath(request.OrganizationId, request.TargetRepositoryId);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var lease = new FileStream(target + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var storage = new InternalGitBackupStorage(_options);
        var manifest = await storage.ReadAsync(request.OrganizationId, request.RepositoryId, request.BackupId, ct) ?? throw new KeyNotFoundException("Backup not found.");
        ValidateBackupManifest(manifest);
        var identity = $"{request.OrganizationId:N}:{request.RepositoryId:N}:{request.BackupId:N}:{manifest.ArchiveSha256}";
        if (Directory.Exists(target))
        {
            var receipt = Path.Combine(target, "csweet-restore-receipt");
            if (!File.Exists(receipt) || await File.ReadAllTextAsync(receipt, ct) != identity) throw new InvalidOperationException("Restore cannot overwrite an existing repository.");
            return BackupSummary(manifest);
        }
        var temporary = NewBackupTemporaryDirectory();
        var staging = target + ".restore-" + Guid.NewGuid().ToString("N");
        try
        {
            var archivePath = Path.Combine(temporary, "archive.zip");
            await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                await storage.DownloadAsync(manifest, output, ct);
                if (output.Length != manifest.ArchiveBytes) throw new InvalidDataException("Backup archive size differs from its manifest.");
                output.Position = 0;
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(output, ct));
                if (!actualHash.Equals(manifest.ArchiveSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Backup archive failed its integrity check.");
            }
            using var zip = ZipFile.OpenRead(archivePath);
            var expectedEntries = manifest.LfsObjects.Select(a => "lfs/" + a.Oid).ToHashSet(StringComparer.Ordinal);
            if (manifest.Refs.Count > 0) expectedEntries.Add("repository.bundle");
            long expanded = 0;
            foreach (var entry in zip.Entries)
            {
                if (!expectedEntries.Remove(entry.FullName)) throw new InvalidDataException("Backup contains duplicate or unexpected entries.");
                expanded = checked(expanded + entry.Length);
                if (expanded > _options.Backup.MaximumObjectBytes) throw new InvalidDataException("Backup expanded size exceeds its configured limit.");
            }
            if (expectedEntries.Count > 0) throw new InvalidDataException("Backup is missing required entries.");
            await RunAsync(null, ["init", "--bare", "--template=", "--initial-branch=" + manifest.DefaultBranch, staging], ct);
            if (manifest.Refs.Count > 0)
            {
                var bundle = Path.Combine(temporary, "repository.bundle");
                await using (var input = zip.GetEntry("repository.bundle")!.Open())
                await using (var output = File.Create(bundle)) await input.CopyToAsync(output, ct);
                await RunAsync(staging, ["bundle", "verify", bundle], ct);
                await RunAsync(staging, ["bundle", "unbundle", bundle], ct);
                var transaction = "start\n" + string.Concat(manifest.Refs.Select(r => $"create {r.Name} {r.Sha}\n")) + "prepare\ncommit\n";
                await RunAsync(staging, ["update-ref", "--stdin"], ct, input: transaction);
            }
            await RunAsync(staging, ["fsck", "--full"], ct);
            // Validate asset coverage from the restored Git graph, not just the manifest's inventory.
            var requiredAssets = manifest.Refs.Count == 0 ? [] : await FindBackupLfsObjectsAsync(staging, ct);
            if (!requiredAssets.OrderBy(a => a.Oid).SequenceEqual(manifest.LfsObjects.OrderBy(a => a.Oid))) throw new InvalidDataException("Backup LFS inventory differs from its Git history.");
            using var lfs = new InternalGitLfsStore(Options.Create(_options));
            foreach (var asset in manifest.LfsObjects)
            {
                var entry = zip.GetEntry("lfs/" + asset.Oid)!;
                if (entry.Length != asset.Size) throw new InvalidDataException("Backup LFS entry has an incorrect size.");
                await using var input = entry.Open(); await lfs.PutAsync(request.OrganizationId, request.TargetRepositoryId, asset.Oid, asset.Size, input, ct);
            }
            await File.WriteAllTextAsync(Path.Combine(staging, "csweet-restore-receipt"), identity, ct);
            Directory.Move(staging, target);
            return BackupSummary(manifest);
        }
        finally
        {
            DeleteOperationDirectory(temporary);
            if (Directory.Exists(staging))
            {
                if (!Path.GetFullPath(staging).StartsWith(Path.GetFullPath(target) + ".restore-", StringComparison.Ordinal)) throw new IOException("Invalid restore staging path.");
                foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(staging, true);
            }
        }
    }
    private static InternalGitBackupSummary BackupSummary(InternalGitBackupManifest m) => new(m.BackupId, m.RepositoryId, m.CreatedAt, m.DefaultBranch,
        m.ArchiveBytes, m.ArchiveSha256, m.Refs.Count, m.LfsObjects.Count);
    private static void ValidateBackupRef(string value)
    {
        if (Regex.IsMatch(value, "\\Arefs/csweet/(publications/[0-9a-f]{64}|merges/[0-9a-f]{32})\\z")) return;
        ValidateRef(value);
    }
    private void ValidateBackupManifest(InternalGitBackupManifest manifest)
    {
        ValidateBranch(manifest.DefaultBranch);
        if (!Regex.IsMatch(manifest.ArchiveSha256, "\\A[0-9a-f]{64}\\z") || manifest.Refs.Count > 100000 || manifest.LfsObjects.Count > 100000 ||
            manifest.Refs.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count() != manifest.Refs.Count ||
            manifest.LfsObjects.Select(a => a.Oid).Distinct(StringComparer.Ordinal).Count() != manifest.LfsObjects.Count)
            throw new InvalidDataException("Backup manifest is invalid.");
        foreach (var reference in manifest.Refs) { ValidateBackupRef(reference.Name); ValidateSha(reference.Sha); }
        foreach (var asset in manifest.LfsObjects)
            if (!Regex.IsMatch(asset.Oid, "\\A[0-9a-f]{64}\\z") || asset.Size < 0 || asset.Size > _options.Lfs.MaximumObjectBytes)
                throw new InvalidDataException("Backup LFS metadata is invalid.");
    }
    private string NewBackupTemporaryDirectory()
    {
        InternalGitStorageOptions.ValidatePath(_options.TemporaryRoot);
        var path = Path.Combine(_options.TemporaryRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path;
    }
    private sealed class BackupArchiveStream(string path, long maximum) : FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)
    {
        private void Check(int count) { if (Position + count > maximum) throw new IOException("Backup exceeds the configured object size limit."); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) { Check(count); return base.WriteAsync(buffer, offset, count, ct); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { Check(buffer.Length); return base.WriteAsync(buffer, ct); }
    }

    private async Task<List<InternalGitBackupLfsObject>> FindBackupLfsObjectsAsync(string repository, CancellationToken ct)
    {
        var objects = await RunAsync(repository, ["rev-list", "--objects", "--all", "--no-object-names"], ct);
        var metadata = await RunAsync(repository, ["cat-file", "--batch-check"], ct, input: objects);
        var candidates = metadata.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim().Split(' '))
            .Where(p => p.Length == 3 && p[1] == "blob" && long.TryParse(p[2], out var size) && size <= 1024).Select(p => p[0]).ToList();
        if (candidates.Count == 0) return [];
        var start = new ProcessStartInfo(_options.GitExecutable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        foreach (var arg in new[] { "-c", "core.longpaths=true", "-c", "protocol.allow=never", "--git-dir", repository, "cat-file", "--batch" }) start.ArgumentList.Add(arg);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(_options.OperationTimeoutSeconds));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git could not start.");
        using var registration = timeout.Token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        var assets = new Dictionary<string, InternalGitBackupLfsObject>(StringComparer.Ordinal);
        async Task ReadAsync()
        {
            try
            {
                var output = process.StandardOutput.BaseStream; var one = new byte[1];
                foreach (var candidate in candidates)
                {
                    var header = new StringBuilder();
                    do { await output.ReadExactlyAsync(one, timeout.Token); if (one[0] == 10) break; header.Append((char)one[0]); if (header.Length > 200) throw new InvalidDataException("Invalid Git object header."); } while (true);
                    var parts = header.ToString().Split(' ');
                    if (parts.Length != 3 || parts[0] != candidate || parts[1] != "blob" || !int.TryParse(parts[2], out var size) || size is < 0 or > 1024) throw new InvalidDataException($"Invalid Git blob response for {candidate}: {header}.");
                    var content = new byte[size]; await output.ReadExactlyAsync(content, timeout.Token); await output.ReadExactlyAsync(one, timeout.Token);
                    if (one[0] != 10) throw new InvalidDataException("Invalid Git blob terminator.");
                    var text = Encoding.UTF8.GetString(content).Replace("\r\n", "\n");
                    if (!text.StartsWith("version https://git-lfs.github.com/spec/v1\n", StringComparison.Ordinal)) continue;
                    var match = Regex.Match(text, "\\Aversion https://git-lfs.github.com/spec/v1\\noid sha256:([0-9a-f]{64})\\nsize ([0-9]+)\\n?\\z");
                    if (!match.Success || !long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var length)) throw new InvalidDataException("Unsupported LFS pointer in Git history.");
                    var asset = new InternalGitBackupLfsObject(match.Groups[1].Value, length);
                    if (assets.TryGetValue(asset.Oid, out var prior) && prior != asset) throw new InvalidDataException("Git history contains inconsistent LFS sizes.");
                    assets[asset.Oid] = asset;
                }
            }
            catch { await timeout.CancelAsync(); throw; }
        }
        async Task WriteAsync() { try { await process.StandardInput.WriteAsync((string.Join('\n', candidates) + "\n").AsMemory(), timeout.Token); process.StandardInput.Close(); } catch { await timeout.CancelAsync(); throw; } }
        async Task<string> ErrorsAsync() { try { return await ReadBoundedAsync(process.StandardError, timeout.Token); } catch { await timeout.CancelAsync(); throw; } }
        var error = ErrorsAsync(); await Task.WhenAll(ReadAsync(), WriteAsync(), error, process.WaitForExitAsync(timeout.Token));
        if (process.ExitCode != 0) throw new InvalidOperationException("Git could not inspect backup objects.");
        return assets.Values.OrderBy(a => a.Oid).ToList();
    }
}
