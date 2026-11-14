using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CSweet.Mobile.Transport;

/// <summary>
/// Inner TLS between phone and Headquarters. Trust is explicit enrollment, not the web PKI: each side accepts the
/// other only by the SHA-256 pin of its public key, plus certificate validity. TLS 1.2 or newer.
/// </summary>
public static class PinnedTls
{
    public const string HeadquartersHostName = "csweet-headquarters";
    private const SslProtocols Protocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    /// <summary>base64url SHA-256 of the certificate's SubjectPublicKeyInfo.</summary>
    public static string Pin(X509Certificate certificate)
    {
        using var certificate2 = certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return Base64Url.Encode(SHA256.HashData(certificate2.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>
    /// A self-signed certificate for <paramref name="key"/>, usable for both server and client authentication.
    /// Re-imported through PKCS#12 because SslStream on Windows cannot use an ephemeral key.
    /// </summary>
    public static X509Certificate2 CreateCertificate(ECDsa key, string subject, TimeSpan validity)
    {
        var request = new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], critical: false));
        var now = DateTimeOffset.UtcNow;
        using var created = request.CreateSelfSigned(now.AddMinutes(-5), now + validity);
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), password: null);
    }

    public static SslClientAuthenticationOptions ClientOptions(string headquartersPin, X509Certificate2 phoneCertificate) => new()
    {
        TargetHost = HeadquartersHostName,
        EnabledSslProtocols = Protocols,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        ClientCertificates = [phoneCertificate],
        RemoteCertificateValidationCallback = (_, certificate, _, errors) => Accept(certificate, errors, pin => pin == headquartersPin)
    };

    public static SslServerAuthenticationOptions ServerOptions(X509Certificate2 headquartersCertificate, Func<string, bool> isEnrolledPhone) => new()
    {
        ServerCertificate = headquartersCertificate,
        EnabledSslProtocols = Protocols,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        ClientCertificateRequired = true,
        RemoteCertificateValidationCallback = (_, certificate, _, errors) => Accept(certificate, errors, isEnrolledPhone)
    };

    private static bool Accept(X509Certificate? certificate, SslPolicyErrors errors, Func<string, bool> pinAllowed)
    {
        // Chain and name errors are expected for self-signed, pinned certificates; a missing certificate is not.
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) return false;
        using var certificate2 = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        var now = DateTime.Now;
        if (now < certificate2.NotBefore || now > certificate2.NotAfter) return false;
        return pinAllowed(Pin(certificate2));
    }
}
