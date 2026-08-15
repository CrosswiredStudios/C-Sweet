using System.Security.Cryptography;
using System.Text;
using CSweet.Application.Setup;
using CSweet.Office.Contracts.Workloads;
using Microsoft.AspNetCore.DataProtection;

namespace CSweet.Infrastructure.Setup;

public sealed class DataProtectionAgentArtifactSigner(IDataProtectionProvider provider) : IAgentArtifactSigner
{
    private readonly IDataProtector _protector = provider.CreateProtector(
        "CSweet.AgentRuntime.ValidatedArtifact.v1");

    public string Sign(string artifactDigest, string provenanceJson)
    {
        var payload = Payload(artifactDigest, provenanceJson);
        return Convert.ToBase64String(_protector.Protect(payload));
    }

    public bool Verify(string artifactDigest, string provenanceJson, string signature)
    {
        try
        {
            var actual = _protector.Unprotect(Convert.FromBase64String(signature));
            return CryptographicOperations.FixedTimeEquals(actual, Payload(artifactDigest, provenanceJson));
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static byte[] Payload(string digest, string provenance) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{digest}\n{provenance}"));
}
