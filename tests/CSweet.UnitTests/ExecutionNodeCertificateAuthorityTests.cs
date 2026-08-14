using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CSweet.Infrastructure.Setup;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class ExecutionNodeCertificateAuthorityTests
{
    [Fact]
    public void Issue_BindsP256KeyToNodeIdentityAndDropsRequestedExtensions()
    {
        var path = TemporaryAuthorityPath();
        try
        {
            using var nodeKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var malicious = new CertificateRequest(
                "CN=Untrusted Requested Subject", nodeKey, HashAlgorithmName.SHA256);
            malicious.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            var authority = CreateAuthority(path);
            var nodeId = Guid.NewGuid();

            var issued = authority.Issue(malicious.CreateSigningRequestPem(), nodeId);

            using var certificate = X509CertificateLoader.LoadCertificate(
                Convert.FromBase64String(issued.CertificateBase64));
            Assert.Equal($"CN=CSweet Execution Node {nodeId:D}", certificate.Subject);
            Assert.False(certificate.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
            Assert.Equal(X509KeyUsageFlags.DigitalSignature,
                certificate.Extensions.OfType<X509KeyUsageExtension>().Single().KeyUsages);
            Assert.Contains(certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single()
                .EnhancedKeyUsages.Cast<Oid>(), oid => oid.Value == "1.3.6.1.5.5.7.3.2");
            var identity = Assert.Single(certificate.Extensions.Cast<X509Extension>(),
                extension => extension.Oid?.Value == "1.3.6.1.4.1.59192.1.1");
            var reader = new AsnReader(identity.RawData, AsnEncodingRules.DER);
            Assert.Equal(nodeId.ToString("D"),
                reader.ReadCharacterString(UniversalTagNumber.UTF8String));
            reader.ThrowIfNotEmpty();
            Assert.Equal(issued.Thumbprint, certificate.Thumbprint);
            Assert.Equal(TimeSpan.Zero, issued.ExpiresAt.Offset);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [Fact]
    public void Issue_RejectsNonP256IdentityKeys()
    {
        var path = TemporaryAuthorityPath();
        try
        {
            using var nodeKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);
            var request = new CertificateRequest("CN=node", nodeKey, HashAlgorithmName.SHA384);

            var exception = Assert.Throws<InvalidDataException>(() =>
                CreateAuthority(path).Issue(request.CreateSigningRequestPem(), Guid.NewGuid()));

            Assert.Contains("P-256", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    private static ExecutionNodeCertificateAuthority CreateAuthority(string path) => new(
        Options.Create(new ExecutionNodeCertificateAuthorityOptions
        {
            AuthorityPfxPath = path,
            AuthorityPfxPassword = "test-only-password",
            OperationalCertificateHours = 8
        }),
        new TestHostEnvironment());

    private static string TemporaryAuthorityPath() => Path.Combine(
        Path.GetTempPath(), $"csweet-ca-{Guid.NewGuid():N}", "authority.pfx");

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "CSweet.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
