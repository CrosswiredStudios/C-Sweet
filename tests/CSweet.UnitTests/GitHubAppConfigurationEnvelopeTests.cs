using System.Security.Cryptography;
using CSweet.TrustedServices;

namespace CSweet.UnitTests;

public sealed class GitHubAppConfigurationEnvelopeTests
{
    [Fact]
    public void SealedCredentialRoundTripsOnlyForTheIntendedHostKind()
    {
        var options = new TrustedServiceAuthenticationOptions
        {
            KeyId = "core",
            SharedKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        };
        var payload = new GitHubAppConfigurationPayload(42, "private-key-material", 7);

        var envelope = GitHubAppConfigurationEnvelope.Seal(payload, options, "source-access");

        Assert.DoesNotContain("private-key-material", envelope.Ciphertext, StringComparison.Ordinal);
        Assert.Equal(payload, GitHubAppConfigurationEnvelope.Open(
            envelope, options, "source-access"));
        Assert.ThrowsAny<CryptographicException>(() => GitHubAppConfigurationEnvelope.Open(
            envelope, options, "provisioner"));
    }
}
