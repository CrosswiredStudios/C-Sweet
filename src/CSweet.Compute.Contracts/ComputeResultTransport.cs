namespace CSweet.Compute.Contracts;

public static class ComputeResultTransport
{
    public const string Path = "/api/compute/providers/results";
    public const int MaximumRequestBytes = 8 * 1024 * 1024;
    public const int MaximumArtifactPayloadCharacters = 6 * 1024 * 1024;
    // Only the outer JSON representation changes; signed PayloadJson bytes and digest stay exact.
    private static readonly System.Text.Json.JsonSerializerOptions EnvelopeJson = new(ComputeProtocol.Json)
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static byte[] SerializeEnvelope(SignedComputeProviderResult envelope) =>
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(envelope, EnvelopeJson);
    public static int PayloadLimit(ComputeProviderResult result) => result.Workload?.Artifact is null ? 32768 : MaximumArtifactPayloadCharacters;
}

public enum ComputeResultDisposition { Recorded, Superseded, Completed }

public sealed record ComputeResultAcknowledgement(Guid OperationId, long Sequence, string PayloadDigest, bool Applied,
    ComputeResultDisposition Disposition = ComputeResultDisposition.Recorded);
