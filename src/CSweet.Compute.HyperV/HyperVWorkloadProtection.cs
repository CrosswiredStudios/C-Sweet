using System.Security.AccessControl;
using System.Security.Principal;
using CSweet.Compute.Runtime;

namespace CSweet.Compute.HyperV;

internal static class HyperVWorkloadProtection
{
    // Hyper-V's worker identity may access only its own workload tree. This exception
    // never applies to provider binaries, templates, credentials or the journal.
    public static void Verify(string path, string workloadRoot, Guid vmId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (vmId == Guid.Empty) throw new UnauthorizedAccessException("An exact VM identity is required.");
        var root = Path.GetFullPath(workloadRoot).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(path);
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The path is outside the owned workload.");
        WindowsComputeProtectedPaths.Verify(Path.GetDirectoryName(root)!);
        var sid = WorkerIdentity(vmId);
        for (var current = full; ; current = Path.GetDirectoryName(current)!)
        {
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!entry.Exists || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("A workload path is missing or redirected.");
            var acl = entry is DirectoryInfo directory ? (FileSystemSecurity)directory.GetAccessControl() : ((FileInfo)entry).GetAccessControl();
            WindowsComputeProtectedPaths.VerifyRules(acl, workloadIdentity: sid);
            if (current.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    // Derive the same SID after VM removal, when account-name lookup may no longer work.
    internal static SecurityIdentifier WorkerIdentity(Guid vmId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (vmId == Guid.Empty) throw new UnauthorizedAccessException("An exact VM identity is required.");
        var bytes = vmId.ToByteArray();
        return new SecurityIdentifier("S-1-5-83-1-" + string.Join("-", Enumerable.Range(0, 4)
            .Select(i => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4, 4)))));
    }
}
