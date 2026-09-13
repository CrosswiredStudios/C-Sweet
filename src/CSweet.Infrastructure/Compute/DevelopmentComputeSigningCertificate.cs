using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CSweet.Infrastructure.Compute;

/// <summary>Shares a persisted, non-exportable local identity across development hosts.</summary>
[SupportedOSPlatform("windows")]
internal static class DevelopmentComputeSigningCertificate
{
    internal static X509Certificate2 Open(string keyName = "CSweet.Compute.ControlPlane.Development")
    {
        using var mutex = new Mutex(false, "Local\\" + keyName);
        try { if (!mutex.WaitOne(TimeSpan.FromSeconds(30))) throw new TimeoutException("Compute identity initialization is busy."); }
        catch (AbandonedMutexException) { }
        try
        {
            using var key = CngKey.Exists(keyName) ? CngKey.Open(keyName) : CngKey.Create(CngAlgorithm.ECDsaP256, keyName,
                new CngKeyCreationParameters { ExportPolicy = CngExportPolicies.None, KeyUsage = CngKeyUsages.Signing });
            using var signingKey = new ECDsaCng(key);
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            var certificates = store.Certificates;
            try
            {
                foreach (var certificate in certificates)
                {
                    using var publicKey = certificate.GetECDsaPublicKey();
                    if (certificate.HasPrivateKey && publicKey is not null &&
                        publicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(signingKey.ExportSubjectPublicKeyInfo()) &&
                        certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(1))
                        return new X509Certificate2(certificate);
                }
                var request = new CertificateRequest("CN=" + keyName, signingKey, HashAlgorithmName.SHA256);
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
                using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
                store.Add(created);
                return new X509Certificate2(created);
            }
            finally { foreach (var certificate in certificates) certificate.Dispose(); }
        }
        finally { mutex.ReleaseMutex(); }
    }
}
