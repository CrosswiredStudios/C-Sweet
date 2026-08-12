using System.Formats.Tar;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CSweet.AgentRuntime.Guest;

/// <summary>
/// Mounts the host-supplied virtual DVD with restrictive flags, verifies the
/// content-addressed bundle, and expands it into the VM's disposable filesystem.
/// No path, device, filesystem type, or mount option is supplied by the workload.
/// </summary>
public sealed partial class GuestArtifactMaterializer
{
    private const string DefaultDevicePath = "/dev/sr0";
    private const string MediaRoot = "/run/csweet/artifact-media";
    private const ulong RestrictiveMountFlags = 1 | 2 | 4 | 8; // RDONLY | NOSUID | NODEV | NOEXEC
    private const int MaximumEntries = 10_000;
    private const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;

    public async Task MaterializeAsync(
        string expectedDigest,
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Guest artifact DVD mounting requires Linux.");
        ValidateDigest(expectedDigest);
        destinationRoot = ValidateDestinationRoot(destinationRoot);
        Directory.CreateDirectory(MediaRoot);
        var devicePath = ResolveDevicePath(
            Environment.GetEnvironmentVariable("CSWEET_GUEST_ARTIFACT_DEVICE"));
        if (Mount(devicePath, MediaRoot, "iso9660", RestrictiveMountFlags, 0) != 0)
            throw new IOException($"The guest artifact DVD could not be mounted (errno {Marshal.GetLastPInvokeError()}).");
        try
        {
            var candidates = Directory.EnumerateFiles(MediaRoot, "*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetFileName(path).StartsWith("artifact.csab", StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            if (candidates.Length != 1)
                throw new InvalidDataException("The guest artifact DVD does not contain exactly one artifact bundle.");
            await using var bundle = new FileStream(
                candidates[0], FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await ExtractValidatedAsync(bundle, expectedDigest, destinationRoot, cancellationToken);
        }
        finally
        {
            if (Unmount(MediaRoot, 0) != 0)
                throw new IOException($"The guest artifact DVD could not be unmounted (errno {Marshal.GetLastPInvokeError()}).");
        }
    }

    internal static async Task ExtractValidatedAsync(
        Stream bundle,
        string expectedDigest,
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ValidateDigest(expectedDigest);
        destinationRoot = ValidateDestinationRoot(destinationRoot);
        if (!bundle.CanRead || !bundle.CanSeek)
            throw new InvalidDataException("The guest artifact bundle must be a readable, seekable stream.");
        var originalPosition = bundle.Position;
        var hash = await SHA256.HashDataAsync(bundle, cancellationToken);
        var actualDigest = "sha256:" + Convert.ToHexStringLower(hash);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actualDigest), Encoding.ASCII.GetBytes(expectedDigest)))
            throw new InvalidDataException("The guest artifact bundle failed its digest verification.");
        bundle.Position = originalPosition;

        ResetDestination(destinationRoot);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entryCount = 0;
        long expandedBytes = 0;
        using var reader = new TarReader(bundle, leaveOpen: true);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            if (++entryCount > MaximumEntries)
                throw new InvalidDataException("The guest artifact contains too many entries.");
            var relative = ValidateEntryName(entry.Name);
            if (!seen.Add(relative))
                throw new InvalidDataException("The guest artifact contains duplicate or case-colliding paths.");
            var target = ResolveBeneath(destinationRoot, relative);
            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
                ApplyMode(target, entry.Mode, isDirectory: true);
                continue;
            }
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
                throw new InvalidDataException("The guest artifact contains a link or special file.");
            if (!relative.Equals("artifact.json", StringComparison.Ordinal) &&
                !relative.StartsWith("payload/", StringComparison.Ordinal))
                throw new InvalidDataException("Guest artifact files must remain beneath payload/.");
            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaximumExpandedBytes)
                throw new InvalidDataException("The guest artifact exceeds its expanded size limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var output = new FileStream(
                target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await entry.DataStream.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            ApplyMode(target, entry.Mode, isDirectory: false);
        }
        if (!File.Exists(Path.Combine(destinationRoot, "artifact.json")) ||
            !Directory.Exists(Path.Combine(destinationRoot, "payload")))
            throw new InvalidDataException("The guest artifact is missing its manifest or payload directory.");
        if (!OperatingSystem.IsWindows())
        {
            foreach (var directory in Directory.EnumerateDirectories(
                         destinationRoot, "*", SearchOption.AllDirectories))
                File.SetUnixFileMode(directory, SanitizeModeForWorkload(default, isDirectory: true));
            await GuestUnixFilePermissions.GrantWorkloadTreeAsync(destinationRoot, cancellationToken);
        }
    }

    private static string ValidateDestinationRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidDataException("The guest artifact destination must be absolute.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath("/run/csweet"));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        var relative = Path.GetRelativePath(root, full);
        if (relative is "." or ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
            throw new InvalidDataException("The guest artifact destination must remain beneath /run/csweet.");
        return full;
    }

    private static string ValidateEntryName(string value)
    {
        var normalized = value.Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') ||
            normalized.Any(char.IsControl) ||
            normalized.Split('/', StringSplitOptions.None).Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("The guest artifact contains an invalid path.");
        return normalized;
    }

    private static string ResolveBeneath(string root, string relative)
    {
        var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("The guest artifact path escaped its destination.");
        return target;
    }

    private static void ResetDestination(string destinationRoot)
    {
        if (Directory.Exists(destinationRoot)) Directory.Delete(destinationRoot, recursive: true);
        Directory.CreateDirectory(destinationRoot);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(destinationRoot, SanitizeModeForWorkload(default, isDirectory: true));
    }

    private static void ApplyMode(string path, UnixFileMode requested, bool isDirectory)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, SanitizeModeForWorkload(requested, isDirectory));
    }

    internal static UnixFileMode SanitizeModeForWorkload(UnixFileMode requested, bool isDirectory)
    {
        if (isDirectory)
            return UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                   UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
        var executable = (requested &
            (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        return UnixFileMode.UserRead | UnixFileMode.GroupRead |
               (executable
                   ? UnixFileMode.UserExecute | UnixFileMode.GroupExecute
                   : 0);
    }

    internal static string ResolveDevicePath(string? configured) => configured switch
    {
        null or "" => DefaultDevicePath,
        "/dev/sr0" => "/dev/sr0",
        "/dev/vdc" => "/dev/vdc",
        _ => throw new InvalidDataException("The guest artifact device is not an approved immutable device.")
    };

    private static void ValidateDigest(string value)
    {
        if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal) ||
            value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") >= 0)
            throw new InvalidDataException("The guest artifact digest is invalid.");
    }

    [LibraryImport("libc", EntryPoint = "mount", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Mount(string source, string target, string fileSystemType, ulong mountFlags, nint data);

    [LibraryImport("libc", EntryPoint = "umount2", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Unmount(string target, int flags);
}
