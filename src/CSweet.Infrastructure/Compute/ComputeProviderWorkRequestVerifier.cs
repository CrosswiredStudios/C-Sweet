using CSweet.Compute.Contracts;
using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Application.Compute;

namespace CSweet.Infrastructure.Compute;

public sealed class ComputeProviderWorkRequestVerifier(IComputeNodeTrust trust, TimeProvider clock)
{
    // Old WebHost assignments and compute mutation authorizations are never work-request evidence.
    internal static byte[] Payload(string json) => ComputeProtocol.WorkRequestPayload(json);

    internal async Task<ComputeProviderWorkRequest> VerifyAsync(SignedComputeProviderWorkRequest envelope, CancellationToken token)
    {
        if (envelope.KeyId is not { Length: > 0 and <= 128 } || envelope.PayloadJson is not { Length: > 0 and <= 32768 } ||
            envelope.SignatureBase64 is not { Length: > 0 and <= 512 })
            throw new UnauthorizedAccessException("Compute work request envelope is invalid.");
        try
        {
            var result = JsonSerializer.Deserialize<ComputeProviderWorkRequest>(envelope.PayloadJson, ComputeBroker.Json)
                ?? throw new UnauthorizedAccessException("Compute work request is missing.");
            var now = clock.GetUtcNow();
            if (result.OrganizationId == Guid.Empty || result.NodeId == Guid.Empty || result.RequestId == Guid.Empty ||
                result.ProviderId is not { Length: > 0 and <= 128 } || result.IssuedAt > now.AddSeconds(5) ||
                result.ExpiresAt <= now || result.ExpiresAt <= result.IssuedAt || result.ExpiresAt - result.IssuedAt > TimeSpan.FromMinutes(1) ||
                result.OperationId == Guid.Empty || result.AfterOperationId == Guid.Empty ||
                result.OperationId.HasValue && result.AfterOperationId.HasValue ||
                result.ResultSequence.HasValue != (result.ResultDigest is not null) ||
                result.ResultSequence.HasValue && (!result.OperationId.HasValue || result.ResultSequence <= 0 ||
                    result.ResultDigest is not { Length: 71 } || !result.ResultDigest.StartsWith("sha256:", StringComparison.Ordinal) ||
                    result.ResultDigest.AsSpan(7).ContainsAnyExcept("0123456789abcdef")))
                throw new UnauthorizedAccessException("Compute work request identity or lifetime is invalid.");
            var pinned = await trust.ResolveAsync(result.OrganizationId, result.NodeId, envelope.KeyId, token);
            if (pinned is null || pinned.KeyId != envelope.KeyId || pinned.NodeId != result.NodeId ||
                pinned.OrganizationId != result.OrganizationId || pinned.ProviderId != result.ProviderId)
                throw new UnauthorizedAccessException("Compute work request enrollment is unavailable.");
            using var key = ECDsa.Create();
            var encoded = Convert.FromBase64String(pinned.SubjectPublicKeyInfoBase64);
            key.ImportSubjectPublicKeyInfo(encoded, out var read);
            if (read != encoded.Length || key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value ||
                !key.VerifyData(Payload(envelope.PayloadJson), Convert.FromBase64String(envelope.SignatureBase64), HashAlgorithmName.SHA256))
                throw new UnauthorizedAccessException("Compute work request signature is invalid.");
            return result;
        }
        catch (Exception error) when (error is JsonException or CryptographicException or FormatException)
        { throw new UnauthorizedAccessException("Compute work request evidence is invalid.", error); }
    }
}
