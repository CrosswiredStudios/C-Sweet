using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;

namespace CSweet.Compute.Runtime;

/// <summary>Installer-owned public enrollment metadata. Contains no private keys or agent-selected host paths.</summary>
public sealed record ComputeProviderConfiguration(int Version, ComputeProviderEnrollment Enrollment,
    ComputeSigningIdentity NodeSigningIdentity, string CoreOrigin, string JournalDirectory, string WorkloadDirectory,
    ComputeProviderCapacity Capacity, string CertificateThumbprint, StoreLocation CertificateStoreLocation,
    string? ProvisioningSettingsPath = null, string? CoreCertificateSha256 = null);

public static class ComputeProviderConfigurationLoader
{
    private const int MaximumBytes = 65536;

    public static Task<ComputeProviderConfiguration> ReadAsync(string path, CancellationToken token) =>
        ReadAsync(path, WindowsComputeProtectedPaths.Verify, token);

    internal static async Task<ComputeProviderConfiguration> ReadAsync(string path, Action<string> protection, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("An absolute provider configuration path is required.");
        protection(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Provider configuration exceeds its size limit.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token);
        var configuration = JsonSerializer.Deserialize<ComputeProviderConfiguration>(bytes, ComputeProtocol.Json)
            ?? throw new InvalidDataException("Provider configuration is missing.");
        Validate(configuration);
        if (Within(path, configuration.JournalDirectory) || Within(path, configuration.WorkloadDirectory))
            throw new InvalidDataException("Provider configuration must be separate from journal and workload roots.");
        protection(configuration.JournalDirectory); protection(configuration.WorkloadDirectory);
        return configuration;
    }

    /// <summary>Open separately from local lease startup: missing delivery credentials must not disable expiry recovery.</summary>
    public static X509Certificate2 OpenSigningCertificate(ComputeProviderConfiguration configuration, TimeProvider clock)
    {
        Validate(configuration);
        using var store = new X509Store(StoreName.My, configuration.CertificateStoreLocation);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var certificates = store.Certificates;
        try { return SelectSigningCertificate(configuration, certificates.Cast<X509Certificate2>(), clock); }
        finally { foreach (var certificate in certificates) certificate.Dispose(); }
    }

    internal static X509Certificate2 SelectSigningCertificate(ComputeProviderConfiguration configuration,
        IEnumerable<X509Certificate2> certificates, TimeProvider clock)
    {
        Validate(configuration);
        var matches = certificates.Where(x => string.Equals(x.Thumbprint, configuration.CertificateThumbprint, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("The enrolled provider signing certificate is unavailable or ambiguous.");
        var certificate = matches[0];
        var now = clock.GetUtcNow().UtcDateTime;
        using var key = certificate.GetECDsaPrivateKey();
        if (key is null || certificate.NotBefore.ToUniversalTime() > now || certificate.NotAfter.ToUniversalTime() < now.AddMinutes(2) ||
            key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value ||
            !CryptographicOperations.FixedTimeEquals(key.ExportSubjectPublicKeyInfo(), Convert.FromBase64String(configuration.NodeSigningIdentity.PublicKeyBase64)))
            throw new InvalidOperationException("Provider certificate must be current, accessible and match the enrolled P-256 identity.");
        return new X509Certificate2(certificate);
    }

    private static void Validate(ComputeProviderConfiguration configuration)
    {
        if (configuration.Version != 1 || configuration.Enrollment is not { OrganizationId: var organizationId, NodeId: var nodeId } enrollment ||
            organizationId == Guid.Empty || nodeId == Guid.Empty || !ComputeSpecification.Identifier(enrollment.ProviderId) ||
            configuration.Capacity is not { IsValid: true } || !Enum.IsDefined(configuration.CertificateStoreLocation) ||
            configuration.CertificateThumbprint is not { Length: 40 } thumbprint || !thumbprint.All(Uri.IsHexDigit) ||
            !Uri.TryCreate(configuration.CoreOrigin, UriKind.Absolute, out var origin) || origin.Scheme != Uri.UriSchemeHttps ||
            origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.AbsolutePath != "/" ||
            string.IsNullOrWhiteSpace(configuration.JournalDirectory) || string.IsNullOrWhiteSpace(configuration.WorkloadDirectory) ||
            !Path.IsPathFullyQualified(configuration.JournalDirectory) || !Path.IsPathFullyQualified(configuration.WorkloadDirectory) ||
            Within(configuration.JournalDirectory, configuration.WorkloadDirectory) || Within(configuration.WorkloadDirectory, configuration.JournalDirectory))
            throw new InvalidDataException("Provider configuration has invalid enrollment, endpoint, capacity or protected roots.");
        if (configuration.CoreCertificateSha256 is { } pin && (pin.Length != 64 || !pin.All(Uri.IsHexDigit)))
            throw new InvalidDataException("The configured Core TLS certificate fingerprint is invalid.");
        ValidateIdentity(enrollment.ControlPlaneKey); ValidateIdentity(configuration.NodeSigningIdentity);
    }

    private static void ValidateIdentity(ComputeSigningIdentity identity)
    {
        if (identity is not { KeyId.Length: > 0 and <= 128, PublicKeyBase64.Length: > 0 and <= 1024 } ||
            string.IsNullOrWhiteSpace(identity.KeyId) || identity.KeyId.Any(char.IsControl))
            throw new InvalidDataException("A pinned P-256 signing identity is required.");
        try
        {
            using var key = ECDsa.Create();
            var bytes = Convert.FromBase64String(identity.PublicKeyBase64);
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                throw new InvalidDataException("A pinned P-256 signing identity is required.");
        }
        catch (Exception error) when (error is FormatException or CryptographicException)
        { throw new InvalidDataException("The pinned signing identity is invalid."); }
    }

    private static bool Within(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
