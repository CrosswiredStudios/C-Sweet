using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;

namespace CSweet.TrustedServices;

internal sealed record InternalGitBackupLfsObject(string Oid, long Size);
internal sealed record InternalGitBackupManifest(int Version, Guid OrganizationId, Guid RepositoryId, Guid BackupId,
    DateTimeOffset CreatedAt, string DefaultBranch, long ArchiveBytes, string ArchiveSha256,
    IReadOnlyList<CSweet.Contracts.SourceControl.InternalGitRef> Refs, IReadOnlyList<InternalGitBackupLfsObject> LfsObjects);

/// <summary>Stores immutable archives with a manifest published last, so interrupted backups are not listed as complete.</summary>
internal sealed class InternalGitBackupStorage(InternalGitStorageOptions storage) : IDisposable
{
    private InternalGitObjectStorageOptions Settings => storage.Backup;
    private IAmazonS3? _client;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string Prefix(Guid business, Guid? repository = null, Guid? backup = null)
    {
        Settings.Validate();
        if (business == Guid.Empty || repository == Guid.Empty || backup == Guid.Empty) throw new ArgumentException("Backup identity is invalid.");
        var prefix = Settings.KeyPrefix.Trim('/');
        if (string.IsNullOrEmpty(prefix) || prefix.Split('/').Any(s => s is "." or ".." || s.Contains('\\') || s.Contains(':')))
            throw new ArgumentException("Backup key prefix is invalid.");
        return $"{prefix}/{business:N}/" + (repository is null ? "" : $"{repository:N}/") + (backup is null ? "" : $"{backup:N}/");
    }
    public async Task<InternalGitBackupManifest?> ReadAsync(Guid business, Guid repository, Guid backup, CancellationToken ct)
    {
        var key = Prefix(business, repository, backup) + "manifest.json";
        using var output = new MemoryStream();
        try { await CopyAsync(key, output, 4 * 1024 * 1024, ct); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
        var manifest = JsonSerializer.Deserialize<InternalGitBackupManifest>(output.ToArray(), Json) ?? throw new InvalidDataException("Backup manifest is invalid.");
        if (manifest.Version != 1 || manifest.OrganizationId != business || manifest.RepositoryId != repository || manifest.BackupId != backup ||
            manifest.ArchiveBytes < 0 || manifest.ArchiveBytes > Settings.MaximumObjectBytes || manifest.Refs is null || manifest.LfsObjects is null)
            throw new InvalidDataException("Backup manifest does not match the requested scope.");
        return manifest;
    }
    public async Task<IReadOnlyList<InternalGitBackupManifest>> ListAsync(Guid business, CancellationToken ct)
    {
        var prefix = Prefix(business);
        var keys = new List<string>();
        if (Settings.Provider == "s3")
        {
            string? continuation = null;
            do
            {
                var page = await Client.ListObjectsV2Async(new() { BucketName = Settings.BucketName, Prefix = prefix, ContinuationToken = continuation }, ct);
                keys.AddRange((page.S3Objects ?? []).Select(o => o.Key).Where(k => k.EndsWith("/manifest.json", StringComparison.Ordinal)));
                if (keys.Count > 1000) throw new InvalidOperationException("Backup catalog exceeds the current 1000-backup inspection limit.");
                continuation = page.IsTruncated == true ? page.NextContinuationToken : null;
            } while (continuation is not null);
        }
        else
        {
            var directory = FilePath(prefix.TrimEnd('/'));
            if (!Directory.Exists(directory)) return [];
            // Only traverse the two generated ID levels, rejecting links before entering them.
            foreach (var repo in Directory.EnumerateDirectories(directory))
            {
                RejectLink(repo);
                if (!Guid.TryParseExact(Path.GetFileName(repo), "N", out _)) continue;
                foreach (var backup in Directory.EnumerateDirectories(repo))
                {
                    RejectLink(backup);
                    if (!Guid.TryParseExact(Path.GetFileName(backup), "N", out _)) continue;
                    if (File.Exists(Path.Combine(backup, "manifest.json"))) keys.Add(prefix + Path.GetFileName(repo) + "/" + Path.GetFileName(backup) + "/manifest.json");
                    if (keys.Count > 1000) throw new InvalidOperationException("Backup catalog exceeds the current 1000-backup inspection limit.");
                }
            }
        }
        var result = new List<InternalGitBackupManifest>();
        foreach (var key in keys)
        {
            var parts = key[prefix.Length..].Split('/');
            if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "N", out var repository) || !Guid.TryParseExact(parts[1], "N", out var backup)) continue;
            var item = await ReadAsync(business, repository, backup, ct); if (item is not null) result.Add(item);
        }
        return result.OrderByDescending(x => x.CreatedAt).ToList();
    }
    public async Task PutAsync(InternalGitBackupManifest manifest, string archive, CancellationToken ct)
    {
        if (new FileInfo(archive).Length > Settings.MaximumObjectBytes) throw new IOException("Backup exceeds the configured object size limit.");
        var prefix = Prefix(manifest.OrganizationId, manifest.RepositoryId, manifest.BackupId);
        await using var content = File.OpenRead(archive);
        await PutObjectAsync(prefix + "archive.zip", content, "application/zip", ct);
        using var metadata = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
        if (metadata.Length > 4 * 1024 * 1024) throw new IOException("Backup manifest exceeds its size limit.");
        await PutObjectAsync(prefix + "manifest.json", metadata, "application/json", ct);
    }
    public Task DownloadAsync(InternalGitBackupManifest manifest, Stream destination, CancellationToken ct) =>
        CopyAsync(Prefix(manifest.OrganizationId, manifest.RepositoryId, manifest.BackupId) + "archive.zip", destination, manifest.ArchiveBytes, ct);
    public async Task DeleteAsync(Guid business, Guid repository, Guid backup, CancellationToken ct)
    {
        var prefix = Prefix(business, repository, backup);
        // Hide the manifest first; interrupted deletion cannot advertise a usable backup with no archive.
        foreach (var key in new[] { prefix + "manifest.json", prefix + "archive.zip" })
        {
            if (Settings.Provider == "s3") await Client.DeleteObjectAsync(Settings.BucketName, key, ct);
            else File.Delete(FilePath(key));
        }
    }
    private async Task CopyAsync(string key, Stream destination, long limit, CancellationToken ct)
    {
        async Task CopyBoundedAsync(Stream input)
        {
            var buffer = new byte[81920]; long total = 0; int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            { total += count; if (total > limit) throw new InvalidDataException("Backup object exceeds its declared size."); await destination.WriteAsync(buffer.AsMemory(0, count), ct); }
        }
        if (Settings.Provider == "s3") { using var result = await Client.GetObjectAsync(Settings.BucketName, key, ct); await CopyBoundedAsync(result.ResponseStream); }
        else { await using var input = File.OpenRead(FilePath(key)); await CopyBoundedAsync(input); }
    }
    private async Task PutObjectAsync(string key, Stream input, string type, CancellationToken ct)
    {
        if (Settings.Provider == "s3")
        {
            using var transfer = new TransferUtility(Client);
            await transfer.UploadAsync(new TransferUtilityUploadRequest { BucketName = Settings.BucketName, Key = key,
                InputStream = input, AutoCloseStream = false, ContentType = type, PartSize = 16 * 1024 * 1024 }, ct);
        }
        else
        {
            var path = FilePath(key); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N");
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { await input.CopyToAsync(output, ct); output.Flush(true); }
                File.Move(temporary, path, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
    }
    private string FilePath(string key)
    {
        var root = Settings.RootPath ?? Path.GetFullPath(Path.Combine(storage.RepositoryRoot, "..", "backups"));
        InternalGitStorageOptions.ValidatePath(root);
        if (Settings.RootPath is not null)
        {
            if (string.IsNullOrWhiteSpace(Settings.ExpectedStoreId) || !File.Exists(Path.Combine(root, ".csweet-object-store")) ||
                File.ReadAllText(Path.Combine(root, ".csweet-object-store")).Trim() != Settings.ExpectedStoreId)
                throw new IOException("Backup storage is unavailable or its identity does not match.");
        }
        else if (storage.ExpectedStoreId is not null && (!File.Exists(Path.Combine(storage.RepositoryRoot, ".csweet-git-store")) ||
            File.ReadAllText(Path.Combine(storage.RepositoryRoot, ".csweet-git-store")).Trim() != storage.ExpectedStoreId))
            throw new IOException("Repository storage is unavailable.");
        var path = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("Invalid backup path.");
        for (var parent = new DirectoryInfo(fullRoot); parent is not null; parent = parent.Parent) RejectLink(parent.FullName);
        var cursor = fullRoot;
        foreach (var part in Path.GetRelativePath(fullRoot, path).Split(Path.DirectorySeparatorChar)) { cursor = Path.Combine(cursor, part); RejectLink(cursor); }
        return path;
    }
    private static void RejectLink(string path)
    { if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Backup storage cannot contain symbolic links."); }
    private IAmazonS3 Client
    {
        get
        {
            if (_client is not null) return _client;
            var config = new AmazonS3Config { RegionEndpoint = RegionEndpoint.GetBySystemName(Settings.Region), ForcePathStyle = Settings.ForcePathStyle };
            if (Settings.ServiceUrl is not null) config.ServiceURL = Settings.ServiceUrl;
            return _client = Settings.AccessKeyId is null ? new AmazonS3Client(config) : new AmazonS3Client(new BasicAWSCredentials(Settings.AccessKeyId, Settings.SecretAccessKey), config);
        }
    }
    public void Dispose() => _client?.Dispose();
}
