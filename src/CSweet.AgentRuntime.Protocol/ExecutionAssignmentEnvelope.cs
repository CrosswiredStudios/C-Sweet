using System.Security.Cryptography;
using System.Text;

namespace CSweet.AgentRuntime.Protocol;

public static class ExecutionAssignmentEnvelope
{
    public static byte[] Payload(
        Guid nodeId,
        Guid assignmentId,
        long fencingEpoch,
        string specificationDigest,
        DateTimeOffset expiresAt,
        string artifactReadToken) => Encoding.UTF8.GetBytes(
        $"csweet-execution-assignment-v1\n{nodeId:D}\n{assignmentId:D}\n{fencingEpoch}\n{specificationDigest}\n{expiresAt.ToUnixTimeSeconds()}\n{TokenDigest(artifactReadToken)}");

    public static string Digest(string specificationJson) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(specificationJson))).ToLowerInvariant()}";

    private static string TokenDigest(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty)));
}
