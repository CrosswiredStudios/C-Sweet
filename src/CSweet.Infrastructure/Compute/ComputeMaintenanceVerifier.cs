using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Application.Compute;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;

namespace CSweet.Infrastructure.Compute;

public sealed class ComputeMaintenanceVerifier(IComputeNodeTrust trust, TimeProvider clock)
{
    internal async Task<(ComputeMaintenanceDelivery Delivery, ComputeMaintenanceEvent Evidence)> VerifyAsync(
        SignedComputeMaintenanceDelivery envelope, CancellationToken token)
    {
        if (envelope is null || envelope.KeyId is not { Length: > 0 and <= 128 } ||
            envelope.PayloadJson is not { Length: > 0 and <= 65536 } || envelope.SignatureBase64 is not { Length: > 0 and <= 512 }) throw Denied();
        try
        {
            var delivery = JsonSerializer.Deserialize<ComputeMaintenanceDelivery>(envelope.PayloadJson, ComputeProtocol.Json) ?? throw Denied();
            var now = clock.GetUtcNow();
            if (delivery.EventJson is not { Length: > 0 and <= 32768 } || delivery.EventDigest != ComputeProtocol.Digest(delivery.EventJson) ||
                delivery.IssuedAt > now.AddSeconds(5) || delivery.ExpiresAt <= now || delivery.ExpiresAt <= delivery.IssuedAt ||
                delivery.ExpiresAt - delivery.IssuedAt > TimeSpan.FromMinutes(5)) throw Denied();
            var evidence = JsonSerializer.Deserialize<ComputeMaintenanceEvent>(delivery.EventJson, ComputeProtocol.Json) ?? throw Denied();
            if (evidence.EventId == Guid.Empty || evidence.NodeId == Guid.Empty || evidence.OrganizationId == Guid.Empty ||
                evidence.EnvironmentId == Guid.Empty || evidence.InstallationId == Guid.Empty || evidence.ProvisionOperationId == Guid.Empty ||
                !ComputeSpecification.Identifier(evidence.ProviderId) || evidence.Generation < 1 || evidence.Attempt < 1 ||
                evidence.OccurredAt > now.AddSeconds(30) || !Enum.IsDefined(evidence.Persistence) || !Enum.IsDefined(evidence.Phase) ||
                evidence.ProvisionGrants is not { Count: > 0 and <= 16 } ||
                evidence.ProvisionGrants.Any(x => x is null || x.GrantId == Guid.Empty || x.Revision < 1 || x.Action is not { Length: > 0 and <= 128 } || x.Action.Any(char.IsControl)) ||
                evidence.ProvisionGrants.Select(x => x.Action).Distinct(StringComparer.Ordinal).Count() != evidence.ProvisionGrants.Count ||
                evidence.Action != (evidence.Persistence == ComputePersistence.Persistent ? InfrastructureActions.Stop : InfrastructureActions.Destroy) ||
                evidence.Phase != ComputeMaintenancePhase.Observed && evidence.PhysicalConfirmed ||
                evidence.FailureCode != (evidence.Phase == ComputeMaintenancePhase.Failed ? "lease-cleanup-uncertain" : null) ||
                evidence.ResourceId is { } resource && (resource.Length is < 1 or > 256 || resource.Any(char.IsControl))) throw Denied();
            var pinned = await trust.ResolveAsync(evidence.OrganizationId, evidence.NodeId, envelope.KeyId, token);
            if (pinned is null || pinned.KeyId != envelope.KeyId || pinned.OrganizationId != evidence.OrganizationId ||
                pinned.NodeId != evidence.NodeId || pinned.ProviderId != evidence.ProviderId) throw Denied();
            using var key = ECDsa.Create(); var bytes = Convert.FromBase64String(pinned.SubjectPublicKeyInfoBase64);
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value ||
                !key.VerifyData(ComputeProtocol.MaintenancePayload(envelope.PayloadJson), Convert.FromBase64String(envelope.SignatureBase64), HashAlgorithmName.SHA256)) throw Denied();
            return (delivery, evidence);
        }
        catch (Exception error) when (error is JsonException or CryptographicException or FormatException)
        { throw new UnauthorizedAccessException("Provider maintenance evidence is invalid.", error); }
    }

    private static UnauthorizedAccessException Denied() => new("Provider maintenance signature, enrollment or evidence is invalid.");
}
