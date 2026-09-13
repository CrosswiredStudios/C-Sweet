using CSweet.Compute.Contracts;
using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Application.Compute;

namespace CSweet.Infrastructure.Compute;

public sealed class ComputeProviderResultVerifier(IComputeNodeTrust trust, TimeProvider clock)
{
    // Old WebHost assignments and compute mutation authorizations are never result evidence.
    internal static byte[] Payload(string json) => ComputeProtocol.ResultPayload(json);

    internal async Task<ComputeProviderResult> VerifyAsync(SignedComputeProviderResult envelope, CancellationToken token)
    {
        if (envelope.KeyId is not { Length: > 0 and <= 128 } || envelope.PayloadJson is not { Length: > 0 and <= 32768 } ||
            envelope.SignatureBase64 is not { Length: > 0 and <= 512 })
            throw new UnauthorizedAccessException("Compute result envelope is invalid.");
        try
        {
            var result = JsonSerializer.Deserialize<ComputeProviderResult>(envelope.PayloadJson, ComputeBroker.Json)
                ?? throw new UnauthorizedAccessException("Compute result is missing.");
            var now = clock.GetUtcNow();
            if (result.OperationId == Guid.Empty || result.EnvironmentId == Guid.Empty || result.OrganizationId == Guid.Empty ||
                result.InstallationId == Guid.Empty || result.NodeId == Guid.Empty || result.Generation < 1 || result.Sequence < 1 ||
                result.ObservedAt > now.AddSeconds(30) || result.ExpiresAt <= now || result.ExpiresAt <= result.ObservedAt ||
                result.ExpiresAt - result.ObservedAt > TimeSpan.FromMinutes(5))
                throw new UnauthorizedAccessException("Compute result identity or lifetime is invalid.");
            var pinned = await trust.ResolveAsync(result.OrganizationId, result.NodeId, envelope.KeyId, token);
            if (pinned is null || pinned.KeyId != envelope.KeyId || pinned.NodeId != result.NodeId ||
                pinned.OrganizationId != result.OrganizationId || pinned.ProviderId != result.ProviderId)
                throw new UnauthorizedAccessException("Compute result enrollment is unavailable.");
            using var key = ECDsa.Create();
            var encoded = Convert.FromBase64String(pinned.SubjectPublicKeyInfoBase64);
            key.ImportSubjectPublicKeyInfo(encoded, out var read);
            if (read != encoded.Length || key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value ||
                !key.VerifyData(Payload(envelope.PayloadJson), Convert.FromBase64String(envelope.SignatureBase64), HashAlgorithmName.SHA256))
                throw new UnauthorizedAccessException("Compute result signature is invalid.");
            return result;
        }
        catch (Exception error) when (error is JsonException or CryptographicException or FormatException)
        { throw new UnauthorizedAccessException("Compute result evidence is invalid.", error); }
    }
}
