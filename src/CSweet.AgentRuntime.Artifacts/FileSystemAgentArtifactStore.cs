using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using CSweet.AgentRuntime.Abstractions;

namespace CSweet.AgentRuntime.Artifacts;

public sealed class FileSystemAgentArtifactStore(
    ArtifactStoreOptions options,
    IAgentArtifactSigner signer) : IAgentArtifactStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root = options.ValidatedRootPath();

    public Task<bool> ExistsAsync(string digest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(PathForDigest(digest)));
    }

    public Task<Stream> OpenReadAsync(string digest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = new FileStream(
            PathForDigest(digest),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public async Task<AgentArtifactReference> ImportAsync(
        Stream content,
        ArtifactImportDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateDigest(descriptor.ExpectedDigest);
        if (descriptor.MaximumBytes is < 1 or > 10L * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(descriptor.MaximumBytes));

        var quarantineRoot = Path.Combine(_root, ".quarantine");
        Directory.CreateDirectory(quarantineRoot);
        var quarantinePath = Path.Combine(quarantineRoot, $"{Guid.NewGuid():N}.bundle");
        try
        {
            var actualDigest = await CopyBoundedAndHashAsync(
                content,
                quarantinePath,
                descriptor.MaximumBytes,
                cancellationToken);
            if (!string.Equals(actualDigest, descriptor.ExpectedDigest, StringComparison.Ordinal))
                throw new InvalidDataException("The exported artifact digest did not match the declared digest.");

            await ValidateBundleAsync(quarantinePath, descriptor, cancellationToken);
            var finalPath = PathForDigest(actualDigest);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            if (File.Exists(finalPath)) File.Delete(quarantinePath);
            else File.Move(quarantinePath, finalPath);

            return new AgentArtifactReference(
                actualDigest,
                signer.Sign(actualDigest, descriptor.ProvenanceJson),
                descriptor.FormatVersion,
                descriptor.OperatingSystem,
                descriptor.Architecture);
        }
        finally
        {
            if (File.Exists(quarantinePath)) File.Delete(quarantinePath);
        }
    }

    private async Task ValidateBundleAsync(
        string path,
        ArtifactImportDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new TarReader(input, leaveOpen: true);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        long total = 0;
        ArtifactBundleManifest? manifest = null;
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            count++;
            if (count > options.MaximumFileCount) throw new InvalidDataException("The artifact contains too many entries.");
            var name = ValidateEntryName(entry.Name);
            if (!names.Add(name)) throw new InvalidDataException("The artifact contains duplicate or case-colliding paths.");
            if (entry.Uid != 0 || entry.Gid != 0) throw new InvalidDataException("Artifact ownership metadata must be normalized.");
            if ((entry.Mode & (UnixFileMode.SetUser | UnixFileMode.SetGroup | UnixFileMode.StickyBit)) != 0)
                throw new InvalidDataException("Artifact entries cannot contain privileged mode bits.");

            if (entry.EntryType is TarEntryType.Directory) continue;
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidDataException("The artifact contains a link or special file.");
            if (!name.Equals("artifact.json", StringComparison.Ordinal) && !name.StartsWith("payload/", StringComparison.Ordinal))
                throw new InvalidDataException("Artifact files must be contained beneath payload/.");

            total = checked(total + entry.Length);
            if (total > options.MaximumUncompressedBytes)
                throw new InvalidDataException("The artifact exceeds the uncompressed size limit.");
            if (name.Equals("artifact.json", StringComparison.Ordinal))
            {
                if (manifest is not null || entry.Length > options.MaximumManifestBytes || entry.DataStream is null)
                    throw new InvalidDataException("The artifact manifest is missing, duplicated, or oversized.");
                manifest = await JsonSerializer.DeserializeAsync<ArtifactBundleManifest>(entry.DataStream, JsonOptions, cancellationToken)
                    ?? throw new InvalidDataException("The artifact manifest is empty.");
            }
        }

        if (manifest is null) throw new InvalidDataException("The artifact does not contain artifact.json.");
        if (!string.Equals(manifest.FormatVersion, descriptor.FormatVersion, StringComparison.Ordinal) ||
            !string.Equals(manifest.OperatingSystem, descriptor.OperatingSystem, StringComparison.Ordinal) ||
            !string.Equals(manifest.Architecture, descriptor.Architecture, StringComparison.Ordinal))
            throw new InvalidDataException("The artifact manifest does not match its import descriptor.");
        if (manifest.Entrypoint is null || manifest.Entrypoint.Count is < 1 or > 32 ||
            manifest.Entrypoint.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > options.MaximumPathLength))
            throw new InvalidDataException("The artifact entrypoint is invalid.");
    }

    private string ValidateEntryName(string name)
    {
        name = name.Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrWhiteSpace(name) || name.Length > options.MaximumPathLength ||
            name.StartsWith('/') || Path.IsPathRooted(name) ||
            name.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or "..") ||
            name.Any(char.IsControl))
            throw new InvalidDataException("The artifact contains an invalid path.");
        return name;
    }

    private async Task<string> CopyBoundedAndHashAsync(Stream input, string outputPath, long maximumBytes, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total = checked(total + read);
            if (total > maximumBytes) throw new InvalidDataException("The artifact export exceeded its byte limit.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        await output.FlushAsync(cancellationToken);
        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private string PathForDigest(string digest)
    {
        ValidateDigest(digest);
        var hex = digest[7..].ToLowerInvariant();
        return Path.Combine(_root, "sha256", hex[..2], $"{hex}.csab");
    }

    private static void ValidateDigest(string digest)
    {
        if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || digest[7..].Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Artifact digests must be lowercase or uppercase sha256 identifiers.", nameof(digest));
    }

    private sealed record ArtifactBundleManifest(
        string FormatVersion,
        string OperatingSystem,
        string Architecture,
        IReadOnlyList<string> Entrypoint);
}
