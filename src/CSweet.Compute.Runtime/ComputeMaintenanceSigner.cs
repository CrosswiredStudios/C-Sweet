using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CSweet.Compute.Contracts;

namespace CSweet.Compute.Runtime;

/// <summary>The service supplies its enrolled OS-held certificate. No private key is exported or generated here.</summary>
public sealed class ComputeMaintenanceSigner(X509Certificate2 certificate, string keyId, TimeProvider clock)
{
    public SignedComputeMaintenanceDelivery Sign(ComputeMaintenanceOutboxEntry entry)
    {
        if (keyId is not { Length: > 0 and <= 128 } || entry.EventJson is not { Length: > 0 and <= 32768 } ||
            entry.Digest != ComputeProtocol.Digest(entry.EventJson)) throw new InvalidDataException("Maintenance outbox evidence is invalid.");
        var evidence = JsonSerializer.Deserialize<ComputeMaintenanceEvent>(entry.EventJson, ComputeProtocol.Json)
            ?? throw new InvalidDataException("Maintenance evidence is missing.");
        if (evidence.EventId != entry.Id || evidence.OccurredAt != entry.OccurredAt)
            throw new InvalidDataException("Maintenance outbox identity is invalid.");
        using var key = certificate.GetECDsaPrivateKey() ?? throw new InvalidOperationException("An enrolled ECDSA signing certificate is required.");
        if (key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            throw new InvalidOperationException("Provider signing requires P-256.");
        var now = clock.GetUtcNow();
        if (certificate.NotBefore.ToUniversalTime() > now.UtcDateTime || certificate.NotAfter.ToUniversalTime() < now.AddMinutes(2).UtcDateTime)
            throw new InvalidOperationException("Maintenance delivery exceeds provider signing-certificate validity.");
        var json = JsonSerializer.Serialize(new ComputeMaintenanceDelivery(entry.EventJson, entry.Digest, now, now.AddMinutes(2)), ComputeProtocol.Json);
        return new(keyId, json, Convert.ToBase64String(key.SignData(ComputeProtocol.MaintenancePayload(json), HashAlgorithmName.SHA256)));
    }
}
