using CSweet.Infrastructure.Compute;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CSweet.UnitTests;

public sealed class DevelopmentComputeSigningTests
{
    [Fact]
    public void DevelopmentIdentitySurvivesReopenAndSignsWithoutExportablePrivateKey()
    {
        if (!OperatingSystem.IsWindows()) return;
        var name = "CSweet.Compute.Test." + Guid.NewGuid().ToString("N");
        string? thumbprint = null;
        try
        {
            byte[] identity;
            using (var first = DevelopmentComputeSigningCertificate.Open(name))
            {
                thumbprint = first.Thumbprint;
                using var privateKey = first.GetECDsaPrivateKey()!;
                identity = privateKey.ExportSubjectPublicKeyInfo();
                Assert.Throws<CryptographicException>(() => privateKey.ExportPkcs8PrivateKey());
            }
            using var second = DevelopmentComputeSigningCertificate.Open(name);
            Assert.Equal(thumbprint, second.Thumbprint);
            using var signer = second.GetECDsaPrivateKey()!;
            Assert.Equal(identity, signer.ExportSubjectPublicKeyInfo());
            var signature = signer.SignData("reopened"u8, HashAlgorithmName.SHA256);
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(identity, out _);
            Assert.True(verifier.VerifyData("reopened"u8, signature, HashAlgorithmName.SHA256));
        }
        finally
        {
            if (thumbprint is not null)
            {
                using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite);
                foreach (var certificate in store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false))
                { store.Remove(certificate); certificate.Dispose(); }
            }
            if (CngKey.Exists(name)) { using var key = CngKey.Open(name); key.Delete(); }
        }
    }
}
