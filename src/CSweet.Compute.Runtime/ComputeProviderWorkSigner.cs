using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CSweet.Compute.Contracts;

namespace CSweet.Compute.Runtime;

public sealed class ComputeProviderWorkSigner(ComputeProviderEnrollment enrollment, X509Certificate2 certificate, string keyId, TimeProvider clock)
{
    public SignedComputeProviderWorkRequest Sign(Guid? operationId = null, Guid? afterOperationId = null, long? resultSequence = null, string? resultDigest = null)
    {
        var now = clock.GetUtcNow(); var expires = now.AddMinutes(1);
        if (enrollment.OrganizationId == Guid.Empty || enrollment.NodeId == Guid.Empty || enrollment.ProviderId is not { Length: > 0 and <= 128 } ||
            keyId is not { Length: > 0 and <= 128 } || operationId == Guid.Empty || afterOperationId == Guid.Empty ||
            operationId.HasValue && afterOperationId.HasValue || resultSequence.HasValue != (resultDigest is not null) ||
            resultSequence.HasValue && (!operationId.HasValue || resultSequence <= 0 || resultDigest is not { Length: 71 } ||
                !resultDigest.StartsWith("sha256:", StringComparison.Ordinal) || resultDigest.AsSpan(7).ContainsAnyExcept("0123456789abcdef")))
            throw new InvalidDataException("Provider work request scope is invalid.");
        if (certificate.NotBefore.ToUniversalTime() > now.UtcDateTime || certificate.NotAfter.ToUniversalTime() < expires.UtcDateTime)
            throw new InvalidOperationException("Provider work request exceeds signing-certificate validity.");
        using var key = certificate.GetECDsaPrivateKey() ?? throw new InvalidOperationException("An enrolled signing key is required.");
        if (key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            throw new InvalidOperationException("Provider signing requires P-256.");
        var request = new ComputeProviderWorkRequest(enrollment.OrganizationId, enrollment.NodeId, enrollment.ProviderId,
            Guid.NewGuid(), now, expires, operationId, afterOperationId, resultSequence, resultDigest);
        var json = JsonSerializer.Serialize(request, ComputeProtocol.Json);
        return new(keyId, json, Convert.ToBase64String(key.SignData(ComputeProtocol.WorkRequestPayload(json), HashAlgorithmName.SHA256)));
    }
}
