using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;

namespace CSweet.Compute.Runtime;

/// <summary>Signs a trusted provider observation without changing its time, sequence or physical claims.</summary>
public sealed class ComputeProviderResultSigner(ComputeProviderEnrollment enrollment, X509Certificate2 certificate,
    string keyId, TimeProvider clock)
{
    public SignedComputeProviderResult Sign(ComputeProviderResult result)
    {
        var now = clock.GetUtcNow();
        if (keyId is not { Length: > 0 and <= 128 } || result.OrganizationId != enrollment.OrganizationId ||
            result.NodeId != enrollment.NodeId || result.ProviderId != enrollment.ProviderId ||
            result.OperationId == Guid.Empty || result.EnvironmentId == Guid.Empty || result.InstallationId == Guid.Empty ||
            result.OrganizationId == Guid.Empty || result.NodeId == Guid.Empty || result.Generation < 1 || result.Sequence < 1 ||
            result.ObservedAt > now.AddSeconds(30) || result.ExpiresAt <= now || result.ExpiresAt <= result.ObservedAt ||
            result.ExpiresAt - result.ObservedAt > TimeSpan.FromMinutes(5) || !Enum.IsDefined(result.State) ||
            result.Action is not (InfrastructureActions.Provision or InfrastructureActions.Start or InfrastructureActions.Stop or InfrastructureActions.Restart or InfrastructureActions.Destroy or InfrastructureActions.Execute or InfrastructureActions.PublishPort) ||
            result.TeardownConfirmed != (result.State == ComputeLifecycleState.Destroyed) ||
            result.TeardownConfirmed && result.Action != InfrastructureActions.Destroy)
            throw new InvalidDataException("Provider observation scope, sequence, lifetime or state is invalid.");
        if (certificate.NotBefore.ToUniversalTime() > now.UtcDateTime || certificate.NotAfter.ToUniversalTime() < result.ExpiresAt.UtcDateTime)
            throw new InvalidOperationException("Provider observation exceeds signing-certificate validity.");
        using var key = certificate.GetECDsaPrivateKey() ?? throw new InvalidOperationException("An enrolled signing key is required.");
        if (key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            throw new InvalidOperationException("Provider signing requires P-256.");
        var json = JsonSerializer.Serialize(result, ComputeProtocol.Json);
        if (json.Length > 32768) throw new InvalidDataException("Provider observation exceeds its size limit.");
        return new(keyId, json, Convert.ToBase64String(key.SignData(ComputeProtocol.ResultPayload(json), HashAlgorithmName.SHA256)));
    }
}
