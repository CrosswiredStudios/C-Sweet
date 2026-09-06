using System.Globalization;
using System.Security.Cryptography;

namespace CSweet.TrustedServices;

public sealed record GitHubLfsObject(string Oid, long Size, string Path);

internal static class GitHubWorkspaceLfs
{
    internal static async Task<List<GitHubLfsObject>> PointersAsync(string directory, CancellationToken ct)
    {
        var result = new List<GitHubLfsObject>(); long total = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (new FileInfo(file).Length > 1024) continue;
            var text = await File.ReadAllTextAsync(file, ct);
            if (!text.StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal)) continue;
            var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length != 3 || lines[0] != "version https://git-lfs.github.com/spec/v1" ||
                !lines[1].StartsWith("oid sha256:", StringComparison.Ordinal) || lines[1].Length != 75 ||
                !lines[1][11..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                !lines[2].StartsWith("size ", StringComparison.Ordinal) ||
                !long.TryParse(lines[2][5..], NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size > 64L * 1024 * 1024)
                throw new InvalidDataException("Unsupported, invalid, or oversized GitHub LFS pointer.");
            total = checked(total + size);
            if (total > 512L * 1024 * 1024) throw new InvalidDataException("GitHub LFS assets exceed the workspace limit.");
            result.Add(new(lines[1][11..], size, file));
        }
        return result;
    }

    internal static string ObjectPath(string storage, string oid) => Path.Combine(storage, "objects", oid[..2], oid[2..4], oid);

    internal static async Task VerifyAsync(string path, GitHubLfsObject asset, CancellationToken ct)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.LinkTarget is not null || file.Length != asset.Size)
            throw new InvalidDataException("GitHub LFS content is missing or has the wrong size.");
        await using var input = File.OpenRead(path);
        if (Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct)) != asset.Oid)
            throw new InvalidDataException("GitHub LFS content does not match its pointer hash.");
    }

    internal static async Task MaterializeAsync(string directory, string cache, GitHubRepositoryDescriptor remote,
        string token, string sha, IGitHubRepositoryTransport transport, CancellationToken ct)
    {
        var pointers = await PointersAsync(directory, ct);
        if (pointers.Count == 0) return;
        var storage = Path.Combine(cache, "csweet-lfs-download");
        await transport.DownloadLfsAsync(cache, remote, token, sha, storage, ct);
        // Verify every object before replacing any pointer in the disposable snapshot.
        foreach (var pointer in pointers) await VerifyAsync(ObjectPath(storage, pointer.Oid), pointer, ct);
        foreach (var pointer in pointers) File.Copy(ObjectPath(storage, pointer.Oid), pointer.Path, true);
    }
}
