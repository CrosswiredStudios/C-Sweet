using System.Security.Cryptography;
using System.Text;
using CSweet.Application.Setup;
using CSweet.SatelliteOffice.Contracts.Workloads;

namespace CSweet.ExecutionArtifacts;

public sealed class HmacAgentArtifactSigner : IAgentArtifactSigner
{
    private readonly byte[] _key;

    public HmacAgentArtifactSigner(string keyBase64)
    {
        try { _key = Convert.FromBase64String(keyBase64); }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("The artifact-signing key is not valid Base64.", exception);
        }
        if (_key.Length < 32) throw new InvalidOperationException("The artifact-signing key must contain at least 32 bytes.");
    }

    public string Sign(string artifactDigest, string provenanceJson) =>
        Convert.ToBase64String(HMACSHA256.HashData(_key, Payload(artifactDigest, provenanceJson)));

    public bool Verify(string artifactDigest, string provenanceJson, string signature)
    {
        byte[] actual;
        try { actual = Convert.FromBase64String(signature); }
        catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(_key, Payload(artifactDigest, provenanceJson));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static byte[] Payload(string digest, string provenance) =>
        Encoding.UTF8.GetBytes($"csweet-agent-artifact-v1\n{digest}\n{provenance}");
}
