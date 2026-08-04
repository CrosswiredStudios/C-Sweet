using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CSweet.TrustedServices;

public sealed record WorkspaceArtifactManifest(
    string Sha256,
    int FileCount,
    long TotalBytes);

public sealed class WorkspaceArtifactLimits
{
    public int MaximumFiles { get; init; } = 20_000;
    public long MaximumTotalBytes { get; init; } = 512L * 1024 * 1024;
    public long MaximumFileBytes { get; init; } = 64L * 1024 * 1024;
    public int MaximumPathLength { get; init; } = 512;
}

/// <summary>
/// Validates complete credential-free workspace snapshots. Symlinks and .git entries are rejected
/// rather than normalized so a snapshot cannot redirect a later bridge or Git operation.
/// </summary>
public sealed class WorkspaceArtifactValidator(WorkspaceArtifactLimits? limits = null)
{
    private static readonly HashSet<string> WindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly WorkspaceArtifactLimits _limits = limits ?? new WorkspaceArtifactLimits();

    public async Task<WorkspaceArtifactManifest> ExtractZipAsync(
        Stream archive,
        string destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<(string RelativePath, string FullPath, long Length)>();
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizeRelativePath(entry.FullName);
            if (relative.Length == 0)
                continue;
            if (!paths.Add(relative))
                throw new InvalidDataException($"The workspace archive contains a duplicate path: {relative}.");
            RejectLink(entry, relative);
            var target = ResolveUnderRoot(root, relative);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            if (entry.Length > _limits.MaximumFileBytes)
                throw new InvalidDataException($"Workspace file {relative} exceeds the per-file limit.");
            total = checked(total + entry.Length);
            if (total > _limits.MaximumTotalBytes)
                throw new InvalidDataException("The workspace archive exceeds the total uncompressed size limit.");
            if (files.Count >= _limits.MaximumFiles)
                throw new InvalidDataException("The workspace archive contains too many files.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var source = entry.Open())
            await using (var output = new FileStream(
                target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyBoundedAsync(source, output, entry.Length, cancellationToken);
            }
            files.Add((relative, target, entry.Length));
        }
        return await ComputeManifestAsync(files, cancellationToken);
    }

    public async Task<WorkspaceArtifactManifest> ValidateDirectoryAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("The workspace snapshot directory was not found.");
        var files = new List<(string RelativePath, string FullPath, long Length)>();
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(current))
            {
                var info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Workspace snapshots cannot contain symbolic links or reparse points.");
                var relative = NormalizeRelativePath(Path.GetRelativePath(root, path));
                if ((info.Attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(path);
                    continue;
                }
                if (info.Length > _limits.MaximumFileBytes)
                    throw new InvalidDataException($"Workspace file {relative} exceeds the per-file limit.");
                total = checked(total + info.Length);
                if (total > _limits.MaximumTotalBytes)
                    throw new InvalidDataException("The workspace snapshot exceeds the total size limit.");
                if (files.Count >= _limits.MaximumFiles)
                    throw new InvalidDataException("The workspace snapshot contains too many files.");
                files.Add((relative, path, info.Length));
            }
        }
        return await ComputeManifestAsync(files, cancellationToken);
    }

    public async Task<WorkspaceArtifactManifest> CreateZipAsync(
        string sourceDirectory,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        var manifest = await ValidateDirectoryAsync(sourceDirectory, cancellationToken);
        var root = Path.GetFullPath(sourceDirectory);
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizeRelativePath(Path.GetRelativePath(root, path));
            var entry = zip.CreateEntry(relative, CompressionLevel.Fastest);
            await using var output = entry.Open();
            await using var input = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, cancellationToken);
        }
        return manifest;
    }

    private string NormalizeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
            return string.Empty;
        var normalized = value.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length > _limits.MaximumPathLength || normalized.StartsWith('/') ||
            Path.IsPathRooted(normalized))
            throw new InvalidDataException("The workspace archive contains an invalid path.");
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
            throw new InvalidDataException("The workspace archive path escapes its workspace.");
        foreach (var segment in segments)
        {
            if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Workspace snapshots cannot contain Git metadata.");
            if (segment.Contains(':') || segment.EndsWith(' ') || segment.EndsWith('.'))
                throw new InvalidDataException("The workspace archive contains a platform-unsafe path.");
            var deviceCandidate = segment.Split('.', 2)[0];
            if (WindowsDeviceNames.Contains(deviceCandidate))
                throw new InvalidDataException("The workspace archive contains a reserved device path.");
        }
        return string.Join('/', segments);
    }

    private static string ResolveUnderRoot(string root, string relative)
    {
        var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
            throw new InvalidDataException("The workspace archive path escapes its workspace.");
        return target;
    }

    private static void RejectLink(ZipArchiveEntry entry, string relative)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixMode == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Workspace entry {relative} is a symbolic link or reparse point.");
    }

    private static async Task CopyBoundedAsync(
        Stream input,
        Stream output,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            copied = checked(copied + read);
            if (copied > expectedLength)
                throw new InvalidDataException("A workspace archive entry expanded beyond its declared size.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (copied != expectedLength)
            throw new InvalidDataException("A workspace archive entry did not match its declared size.");
    }

    private static async Task<WorkspaceArtifactManifest> ComputeManifestAsync(
        IReadOnlyList<(string RelativePath, string FullPath, long Length)> files,
        CancellationToken cancellationToken)
    {
        using var manifestHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        foreach (var file in files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            total = checked(total + file.Length);
            var header = Encoding.UTF8.GetBytes($"{file.RelativePath}\0{file.Length}\0");
            manifestHash.AppendData(header);
            await using var input = new FileStream(
                file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                manifestHash.AppendData(buffer, 0, read);
        }
        return new WorkspaceArtifactManifest(
            Convert.ToHexString(manifestHash.GetHashAndReset()).ToLowerInvariant(),
            files.Count,
            total);
    }
}
