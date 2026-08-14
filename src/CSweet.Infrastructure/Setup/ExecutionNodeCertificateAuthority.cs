using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Formats.Asn1;
using CSweet.Application.Setup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

public sealed class ExecutionNodeCertificateAuthorityOptions
{
    public const string SectionName = "CSweet:ExecutionNodeCertificates";
    public string AuthorityPfxPath { get; set; } = string.Empty;
    public string AuthorityPfxPassword { get; set; } = string.Empty;
    public int OperationalCertificateHours { get; set; } = 24;
}

public sealed class ExecutionNodeCertificateAuthority(
    IOptions<ExecutionNodeCertificateAuthorityOptions> options,
    IHostEnvironment environment) : IExecutionNodeCertificateAuthority
{
    private readonly object _gate = new();
    private X509Certificate2? _authority;

    public IssuedExecutionNodeCertificate Issue(string certificateSigningRequestPem, Guid nodeId)
    {
        if (nodeId == Guid.Empty || string.IsNullOrWhiteSpace(certificateSigningRequestPem) ||
            certificateSigningRequestPem.Length > 16 * 1024)
            throw new InvalidDataException("The execution-node certificate request is invalid.");
        CertificateRequest request;
        try
        {
            request = CertificateRequest.LoadSigningRequestPem(
                certificateSigningRequestPem,
                HashAlgorithmName.SHA256,
                CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("The execution-node certificate request is malformed.", exception);
        }
        if (request.PublicKey.Oid.Value != "1.2.840.10045.2.1")
            throw new InvalidDataException("Execution nodes must use an ECDSA P-256 identity key.");
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(request.PublicKey.ExportSubjectPublicKeyInfo(), out _);
            if (key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                throw new InvalidDataException("Execution nodes must use an ECDSA P-256 identity key.");
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("The execution-node public key is invalid.", exception);
        }
        var authority = Authority();
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddHours(Math.Clamp(options.Value.OperationalCertificateHours, 1, 168));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7f;
        var issuedRequest = new CertificateRequest(
            new X500DistinguishedName($"CN=CSweet Execution Node {nodeId:D}"),
            request.PublicKey,
            HashAlgorithmName.SHA256);
        issuedRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        issuedRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature, true));
        var clientAuthentication = new OidCollection { new("1.3.6.1.5.5.7.3.2") };
        issuedRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(clientAuthentication, true));
        issuedRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        issuedRequest.CertificateExtensions.Add(NodeIdentityExtension(nodeId));
        using var issued = issuedRequest.Create(authority, now.AddMinutes(-5), expires, serial);
        return new IssuedExecutionNodeCertificate(
            Convert.ToBase64String(issued.Export(X509ContentType.Cert)),
            issued.Thumbprint,
            issued.SerialNumber,
            new DateTimeOffset(issued.NotAfter.ToUniversalTime()));
    }

    private static X509Extension NodeIdentityExtension(Guid nodeId)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.WriteCharacterString(UniversalTagNumber.UTF8String, nodeId.ToString("D"));
        return new X509Extension("1.3.6.1.4.1.59192.1.1", writer.Encode(), true);
    }

    private X509Certificate2 Authority()
    {
        lock (_gate)
        {
            if (_authority is not null) return _authority;
            var configured = options.Value.AuthorityPfxPath;
            var path = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CSweet", "control-plane", "execution-node-ca.pfx")
                : Path.GetFullPath(configured);
            if (File.Exists(path))
                return _authority = X509CertificateLoader.LoadPkcs12FromFile(
                    path, options.Value.AuthorityPfxPassword,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
                throw new InvalidOperationException(
                    "A shared execution-node certificate authority must be configured outside development.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest(
                "CN=C-Sweet Private Execution Node CA", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using var generated = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            var pfx = generated.Export(X509ContentType.Pfx, options.Value.AuthorityPfxPassword);
            File.WriteAllBytes(path, pfx);
            return _authority = X509CertificateLoader.LoadPkcs12(
                pfx, options.Value.AuthorityPfxPassword,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        }
    }
}
