using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CSweet.Compute.Runtime;

namespace CSweet.UnitTests;

public sealed class ComputeLocalTlsTests
{
    [Fact]
    public void Local_pin_accepts_only_the_current_named_certificate_and_default_validation_remains_enabled()
    {
        using var key = ECDsa.Create();
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var other = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(2));
        using var expired = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1));
        using var standard = ComputeMaintenanceHttpClient.CreateHandler();
        Assert.Null(standard.ServerCertificateCustomValidationCallback);
        using var pinned = ComputeMaintenanceHttpClient.CreateHandler(certificate.GetCertHashString(HashAlgorithmName.SHA256));
        var validate = pinned.ServerCertificateCustomValidationCallback!;
        using var message = new HttpRequestMessage();
        Assert.True(validate(message, certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(validate(message, other, null, SslPolicyErrors.None));
        Assert.False(validate(message, certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(validate(message, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
        using var expiredPin = ComputeMaintenanceHttpClient.CreateHandler(expired.GetCertHashString(HashAlgorithmName.SHA256));
        Assert.False(expiredPin.ServerCertificateCustomValidationCallback!(message, expired, null, SslPolicyErrors.None));
    }
}
