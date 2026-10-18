using System.Security.Cryptography;

namespace CSweet.Compute.Contracts;

/// <summary>Immutable public npm tarballs. The provider never unpacks or executes them on the host.</summary>
public sealed record ComputeNodePackage(string Url, string Integrity);
public sealed record ComputeNodeDependencies(string LockfileSha256, IReadOnlyList<ComputeNodePackage> Packages)
{
    public const string Registry = "registry.npmjs.org";
    public const int MaximumPackages = 256;
    public const int MaximumPackageBytes = 32 * 1024 * 1024;
    public const long MaximumTotalBytes = 192L * 1024 * 1024;
    [System.Text.Json.Serialization.JsonIgnore]
    public string GuestDirectory => "/var/lib/csweet-compute/work/npm-" + LockfileSha256;
    public ComputeNodeDependencies Validate()
    {
        if (LockfileSha256 is not { Length: 64 } || LockfileSha256.Any(c => !"0123456789abcdef".Contains(c)) ||
            Packages is null || Packages.Count > MaximumPackages) throw new ArgumentException("A bounded lockfile dependency set is required.");
        var packages = Packages.ToArray();
        foreach (var package in packages)
        {
            if (package is null || !Uri.TryCreate(package.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.Host != Registry || uri.Port != 443 || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "" ||
                !uri.AbsolutePath.EndsWith(".tgz", StringComparison.Ordinal) || package.Url.Length > 1024 ||
                package.Integrity is not { Length: 95 } || !package.Integrity.StartsWith("sha512-", StringComparison.Ordinal))
                throw new ArgumentException("Dependency restore supports integrity-pinned public npm tarballs only.");
            try { if (Convert.FromBase64String(package.Integrity[7..]).Length != 64) throw new FormatException(); }
            catch (FormatException) { throw new ArgumentException("Invalid dependency integrity."); }
        }
        if (packages.Select(p => p.Url).Distinct(StringComparer.Ordinal).Count() != packages.Length)
            throw new ArgumentException("Duplicate dependency URLs are ambiguous.");
        return this with { Packages = Array.AsReadOnly(packages) };
    }
    public static string FileName(ComputeNodePackage package) => Convert.ToHexStringLower(
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(package.Url + "\n" + package.Integrity))) + ".tgz";
}
