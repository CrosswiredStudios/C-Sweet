using CSweet.Compute.Contracts;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CSweet.Application.Compute;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Compute;

public sealed class ComputeSigningOptions
{
    public const string SectionName = "CSweet:ComputeSigning";
    public bool AutoProvisionDevelopmentCertificate { get; set; }
    public string CertificateThumbprint { get; set; } = "";
    public StoreLocation StoreLocation { get; set; } = StoreLocation.CurrentUser;
}

/// <summary>Uses an operator-installed certificate key without exporting or persisting private material.</summary>
public sealed class ComputeDispatchSigner(IOptions<ComputeSigningOptions> options, TimeProvider clock) : IComputeDispatchSigner
{
    internal static byte[] Payload(string json) => ComputeProtocol.DispatchPayload(json);

    public Task<SignedComputeDispatch> SignAsync(ComputeDispatchAuthorization authorization, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var certificate = Certificate();
        using var key = certificate.GetECDsaPrivateKey() ?? throw new InvalidOperationException("The compute signing certificate has no private signing key.");
        if (authorization.ExpiresAt <= clock.GetUtcNow() || authorization.ExpiresAt > new DateTimeOffset(certificate.NotAfter.ToUniversalTime()))
            throw new InvalidOperationException("The compute authorization exceeds signing-key validity.");
        var json = JsonSerializer.Serialize(authorization, ComputeBroker.Json);
        return Task.FromResult(new SignedComputeDispatch(Identity(certificate).KeyId, json,
            Convert.ToBase64String(key.SignData(Payload(json), HashAlgorithmName.SHA256))));
    }

    public Task<ComputeSigningIdentity> GetIdentityAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); using var certificate = Certificate();
        return Task.FromResult(Identity(certificate));
    }

    private X509Certificate2 Certificate()
    {
        var thumbprint = options.Value.CertificateThumbprint;
        if (string.IsNullOrWhiteSpace(thumbprint) && options.Value.AutoProvisionDevelopmentCertificate && OperatingSystem.IsWindows())
            return DevelopmentComputeSigningCertificate.Open();
        if (string.IsNullOrWhiteSpace(thumbprint) || !Enum.IsDefined(options.Value.StoreLocation))
            throw new InvalidOperationException("Configure a compute signing certificate before dispatch.");
        using var store = new X509Store(StoreName.My, options.Value.StoreLocation);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        try
        {
            if (matches.Count != 1) throw new InvalidOperationException("The configured compute signing certificate is unavailable or ambiguous.");
            var certificate = matches[0];
            using var key = certificate.GetECDsaPublicKey();
            if (!certificate.HasPrivateKey || key is null || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value ||
                certificate.NotBefore.ToUniversalTime() > clock.GetUtcNow().UtcDateTime || certificate.NotAfter.ToUniversalTime() <= clock.GetUtcNow().UtcDateTime)
                throw new InvalidOperationException("The compute signing certificate must contain a current P-256 signing key.");
            return new X509Certificate2(certificate);
        }
        finally { foreach (var match in matches) match.Dispose(); }
    }

    private static ComputeSigningIdentity Identity(X509Certificate2 certificate)
    {
        using var key = certificate.GetECDsaPublicKey()!;
        var encoded = key.ExportSubjectPublicKeyInfo();
        return new("sha256:" + Convert.ToHexStringLower(SHA256.HashData(encoded)), Convert.ToBase64String(encoded));
    }
}
