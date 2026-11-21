using System.Runtime.InteropServices;
using System.Text;

namespace CSweet.Compute.Runtime;

/// <summary>Installer-owned Windows layout, independent of Office removal and business retirement.</summary>
public static class ComputeVolumeStorage
{
    public static string Root(char drive) => char.IsAsciiLetter(drive)
        ? $@"{char.ToUpperInvariant(drive)}:\CSweetCompute"
        : throw new ArgumentException("A local drive letter is required.", nameof(drive));

    public static string Workloads(char drive, Guid business, Guid node) =>
        Path.Combine(Root(drive), $"workloads-{business:N}-{node:N}");

    public static bool IsVolumeWorkload(string path, Guid business, Guid node) =>
        OperatingSystem.IsWindows() && path.Length > 0 && char.IsAsciiLetter(path[0]) &&
        string.Equals(path, Workloads(path[0], business, node), StringComparison.OrdinalIgnoreCase);

    internal static void VerifyMountedVolume(string path, string expectedId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Compute storage is required.");
        var volume = new StringBuilder(1024);
        if (!GetVolumeNameForVolumeMountPoint(Path.GetPathRoot(path)!, volume, volume.Capacity) ||
            !string.Equals(expectedId, volume.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The enrolled Compute volume is missing or has been replaced.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mount, StringBuilder name, int length);
}
