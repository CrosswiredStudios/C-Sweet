using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using CSweet.Compute.Runtime;

namespace CSweet.Compute.HyperV;

/// <summary>Installer-owned immutable VHDX files shared by business providers on this host.</summary>
internal sealed class ComputeSharedImageStore
{
    // A sibling of both provider roots: business retirement must never own this directory.
    internal static string InstallationRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CSweet", "ComputeImages");

    private readonly string root;
    private readonly Action<string> ensureDirectory;
    private readonly Action<string> protectFile;
    private readonly Action<string> verify;

    public ComputeSharedImageStore() : this(InstallationRoot, EnsureProtectedDirectory,
        ProtectFile, WindowsComputeProtectedPaths.Verify) { }

    // Tests use isolated directories without changing machine ACLs or requiring elevation.
    internal ComputeSharedImageStore(string root, Action<string> ensureDirectory,
        Action<string> protectFile, Action<string> verify)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("An absolute image store is required.", nameof(root));
        this.root = Path.GetFullPath(root);
        this.ensureDirectory = ensureDirectory;
        this.protectFile = protectFile;
        this.verify = verify;
    }

    public async Task<string> ImportAsync(string source, string digest, CancellationToken token)
    {
        if (digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            digest.AsSpan(7).ContainsAnyExcept("0123456789abcdef"))
            throw new ArgumentException("A canonical SHA-256 image digest is required.", nameof(digest));
        if (!Path.IsPathFullyQualified(source)) throw new ArgumentException("An absolute image source is required.", nameof(source));
        token.ThrowIfCancellationRequested();
        ensureDirectory(root);
        verify(root);
        var destination = Path.Combine(root, digest[7..] + ".vhdx");
        if (File.Exists(destination))
        {
            await VerifyImageAsync(destination, digest, token);
            return destination;
        }

        // Hold the source against writes/deletion, stage privately, then publish with a
        // no-overwrite rename. Concurrent installs may stage, but only one publishes.
        var temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".incoming");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, token);
                await output.FlushAsync(token);
                output.Flush(flushToDisk: true);
            }
            protectFile(temporary);
            await VerifyImageAsync(temporary, digest, token);
            token.ThrowIfCancellationRequested();
            try { File.Move(temporary, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination))
            {
                // Another installer won publication. Never replace a published image,
                // including a corrupt one which may already be referenced by a provider.
            }
            await VerifyImageAsync(destination, digest, token);
            return destination;
        }
        finally
        {
            // Only this operation's private staging file; never a published image.
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task VerifyImageAsync(string path, string digest, CancellationToken token)
    {
        verify(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = "sha256:" + Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
        if (!StringComparer.Ordinal.Equals(actual, digest))
            throw new InvalidDataException("The shared compute image does not match its digest.");
    }

    private static void EnsureProtectedDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The Hyper-V image store requires Windows ACLs.");
        // Do not repair/re-own an existing untrusted directory or follow redirected ancestors.
        for (var parent = Directory.GetParent(path); parent is not null; parent = parent.Parent)
        {
            if (!parent.Exists || (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("The image store parent is missing or redirects elsewhere.");
            WindowsComputeProtectedPaths.VerifyRules(parent.GetAccessControl(), ancestor: true);
        }
        if (!Directory.Exists(path))
        {
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            acl.SetOwner(administrators);
            foreach (var sid in new[] { administrators, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                acl.AddAccessRule(new(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(acl);
        }
        WindowsComputeProtectedPaths.Verify(path);
    }

    private static void ProtectFile(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The Hyper-V image store requires Windows ACLs.");
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(true, false);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        acl.SetOwner(administrators);
        foreach (var sid in new[] { administrators, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
    }
}
