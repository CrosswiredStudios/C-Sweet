using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace CSweet.TrustedServices;

/// <summary>Private, repository-scoped LFS content. Uploads are verified before becoming visible.</summary>
public sealed class InternalGitLfsStore(IOptions<InternalGitStorageOptions> options) : IDisposable
{
    private readonly InternalGitStorageOptions _storage = options.Value;
    private IAmazonS3? _client;
    private InternalGitObjectStorageOptions Settings => _storage.Lfs;

    public async Task PutAsync(Guid business, Guid repository, string oid, long size, Stream content, CancellationToken ct = default)
    {
        var key = Key(business, repository, oid);
        if (size < 0 || size > Settings.MaximumObjectBytes) throw new ArgumentException("LFS object exceeds the configured limit.");
        InternalGitStorageOptions.ValidatePath(_storage.TemporaryRoot);
        Directory.CreateDirectory(_storage.TemporaryRoot);
        var temporary = Path.Combine(_storage.TemporaryRoot, "lfs-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int count;
            while ((count = await content.ReadAsync(buffer, ct)) > 0)
            {
                total = checked(total + count);
                if (total > size) throw new InvalidDataException("LFS upload exceeds its declared size.");
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            if (total != size || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), oid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("LFS object content does not match its SHA-256 and size.");
            output.Flush(flushToDisk: true);
            output.Position = 0;
            if (Settings.Provider == "s3")
            {
                var request = new PutObjectRequest
                {
                    BucketName = Settings.BucketName, Key = key, InputStream = output, AutoCloseStream = false,
                    ContentType = "application/octet-stream"
                };
                request.Metadata["csweet-sha256"] = oid;
                await Client.PutObjectAsync(request, ct);
            }
            else
            {
                var destination = FilePath(key);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var incoming = destination + "." + Guid.NewGuid().ToString("N");
                try
                {
                    await using (var target = new FileStream(incoming, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { await output.CopyToAsync(target, ct); target.Flush(flushToDisk: true); }
                    File.Move(incoming, destination, overwrite: true);
                }
                finally { File.Delete(incoming); }
            }
        }
        finally { File.Delete(temporary); }
    }

    public async Task CopyToAsync(Guid business, Guid repository, string oid, Stream destination, CancellationToken ct = default, long? expectedSize = null)
    {
        var key = Key(business, repository, oid);
        if (Settings.Provider == "s3")
        {
            using var response = await Client.GetObjectAsync(Settings.BucketName, key, ct);
            await CopyVerifiedAsync(response.ResponseStream, destination, oid, ct, expectedSize);
        }
        else
        {
            await using var source = File.OpenRead(FilePath(key));
            await CopyVerifiedAsync(source, destination, oid, ct, expectedSize);
        }
    }

    private async Task CopyVerifiedAsync(Stream source, Stream destination, string oid, CancellationToken ct, long? expectedSize)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, ct)) > 0)
        {
            total = checked(total + count);
            if (total > Settings.MaximumObjectBytes) throw new InvalidDataException("Stored LFS object exceeds the configured size limit.");
            hash.AppendData(buffer, 0, count);
            await destination.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (expectedSize is not null && total != expectedSize) throw new InvalidDataException("LFS object size differs from its pointer.");
        if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), oid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Stored LFS object failed its SHA-256 integrity check.");
    }

    private string Key(Guid business, Guid repository, string oid)
    {
        Settings.Validate();
        if (business == Guid.Empty || repository == Guid.Empty || !Regex.IsMatch(oid, "\\A[0-9a-f]{64}\\z"))
            throw new ArgumentException("A business, repository and lowercase SHA-256 object ID are required.");
        return $"{Settings.KeyPrefix.Trim('/')}/{business:N}/{repository:N}/{oid[..2]}/{oid}";
    }

    private string FilePath(string key)
    {
        var root = Settings.RootPath ?? Path.GetFullPath(Path.Combine(_storage.RepositoryRoot, "..", "lfs"));
        InternalGitStorageOptions.ValidatePath(root);
        if (Settings.RootPath is not null)
        {
            if (string.IsNullOrWhiteSpace(Settings.ExpectedStoreId) || !File.Exists(Path.Combine(root, ".csweet-object-store")) ||
                File.ReadAllText(Path.Combine(root, ".csweet-object-store")).Trim() != Settings.ExpectedStoreId)
                throw new IOException("LFS storage is unavailable or its identity does not match.");
        }
        else
        {
            // The default LFS root follows repository storage; require the configured repository marker for NAS roots.
            if (_storage.ExpectedStoreId is not null && (!File.Exists(Path.Combine(_storage.RepositoryRoot, ".csweet-git-store")) ||
                File.ReadAllText(Path.Combine(_storage.RepositoryRoot, ".csweet-git-store")).Trim() != _storage.ExpectedStoreId))
                throw new IOException("Repository storage is unavailable.");
            Directory.CreateDirectory(root);
        }
        var path = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invalid LFS storage path.");
        return path;
    }

    private IAmazonS3 Client => _client ??= CreateClient();
    private IAmazonS3 CreateClient()
    {
        var configuration = new AmazonS3Config { RegionEndpoint = RegionEndpoint.GetBySystemName(Settings.Region), ForcePathStyle = Settings.ForcePathStyle };
        if (Settings.ServiceUrl is not null) configuration.ServiceURL = Settings.ServiceUrl;
        return Settings.AccessKeyId is null ? new AmazonS3Client(configuration)
            : new AmazonS3Client(new BasicAWSCredentials(Settings.AccessKeyId, Settings.SecretAccessKey), configuration);
    }
    public void Dispose() => _client?.Dispose();
}
