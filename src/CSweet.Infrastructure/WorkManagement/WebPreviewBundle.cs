using System.Formats.Tar;
using System.Security.Cryptography;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

/// <summary>Verified static web files from an immutable, ingested build output bundle.</summary>
public sealed record WebPreviewFile(byte[] Content, string ContentType);
public sealed record WebPreviewBundle(IReadOnlyDictionary<string, WebPreviewFile> Files)
{
    public const long MaximumTotalBytes = 256L * 1024 * 1024;
    public const long MaximumFileBytes = 64L * 1024 * 1024;
    public const int MaximumFiles = 4096;

    public static async Task<WebPreviewBundle> ReadAsync(Stream archive,
        IReadOnlyList<BuildOutputManifestEntry> manifest, CancellationToken token = default)
    {
        if (manifest.Count is 0 or > MaximumFiles)
            throw new InvalidDataException("A web preview needs a bounded output manifest.");
        var expected = new Dictionary<string, BuildOutputManifestEntry>(StringComparer.Ordinal);
        long total = 0;
        foreach (var file in manifest)
        {
            if (!ValidPath(file.RelativePath) || !expected.TryAdd(file.RelativePath, file) ||
                file.Size < 0 || file.Size > MaximumFileBytes || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit) ||
                string.IsNullOrWhiteSpace(file.ContentType) || file.ContentType.Any(char.IsControl))
                throw new InvalidDataException("The web build manifest contains an invalid file.");
            total += file.Size;
            if (total > MaximumTotalBytes) throw new InvalidDataException("The web preview exceeds its output budget.");
        }
        if (!expected.TryGetValue("index.html", out var index) ||
            !index.ContentType.Split(';')[0].Trim().Equals("text/html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A static web preview requires an HTML index.html at its output root.");
        var files = new Dictionary<string, WebPreviewFile>(StringComparer.Ordinal);
        using var reader = new TarReader(archive, leaveOpen: true);
        while (await reader.GetNextEntryAsync(copyData: false, token) is { } entry)
        {
            token.ThrowIfCancellationRequested();
            // Build bundles contain additional non-public provenance outside payload/output.
            if (!entry.Name.StartsWith("payload/output/", StringComparison.Ordinal)) continue;
            var path = entry.Name["payload/output/".Length..];
            if (entry.EntryType == TarEntryType.Directory && path.EndsWith('/') && ValidPath(path.TrimEnd('/'))) continue;
            if (!ValidPath(path) || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
                entry.DataStream is null || !expected.TryGetValue(path, out var declared) || files.ContainsKey(path) ||
                entry.Length != declared.Size)
                throw new InvalidDataException("The web output archive does not match its declared regular files.");
            var bytes = new byte[checked((int)entry.Length)];
            await entry.DataStream.ReadExactlyAsync(bytes, token);
            if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(declared.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A web output file does not match its recorded digest.");
            files.Add(path, new(bytes, declared.ContentType));
        }
        if (files.Count != expected.Count) throw new InvalidDataException("The web output archive is missing declared files.");
        return new(files);
    }

    public static bool ValidPath(string path) => !string.IsNullOrWhiteSpace(path) && path.Length <= 1024 &&
        !path.Any(char.IsControl) && !path.Contains('\\') && !path.Contains(':') && !path.Contains('%') &&
        !path.Contains('?') && !path.Contains('#') &&
        path.Split('/').All(part => part.Length > 0 && part is not "." and not "..");
}
