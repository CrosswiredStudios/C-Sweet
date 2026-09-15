using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;

namespace CSweet.Compute.Runtime;

public sealed record ComputeProviderEnrollment(Guid OrganizationId, Guid NodeId, string ProviderId, ComputeSigningIdentity ControlPlaneKey);

/// <summary>Can only be constructed by the privileged runtime's verifier.</summary>
public sealed class VerifiedComputeDispatch
{
    internal VerifiedComputeDispatch(ComputeDispatchAuthorization authorization, ComputeSpecification specification, ComputeTemplate? template, ComputeWorkload? workload = null)
    { Authorization = authorization; Specification = specification; Template = template; Workload = workload; }
    public ComputeDispatchAuthorization Authorization { get; }
    public ComputeSpecification Specification { get; }
    public ComputeTemplate? Template { get; }
    public ComputeWorkload? Workload { get; }
}

/// <summary>No Core database, broker, agent SDK or host commands are used to verify a provider request.</summary>
public sealed class ComputeDispatchVerifier(ComputeProviderEnrollment enrollment, ComputeProviderCapabilities capabilities,
    ComputeResources maximumResources, IReadOnlyDictionary<string, ComputeTemplate> certifiedTemplates, TimeProvider clock)
{
    public VerifiedComputeDispatch Verify(ComputeDispatchPacket packet)
    {
        if (packet?.Authorization is not { } envelope || envelope.KeyId != enrollment.ControlPlaneKey.KeyId ||
            envelope.PayloadJson is not { Length: > 0 and <= 32768 } || envelope.SignatureBase64 is not { Length: > 0 and <= 512 })
            throw Denied();
        try
        {
            using var key = ECDsa.Create();
            var encoded = Convert.FromBase64String(enrollment.ControlPlaneKey.PublicKeyBase64);
            key.ImportSubjectPublicKeyInfo(encoded, out var read);
            if (read != encoded.Length || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value ||
                !key.VerifyData(ComputeProtocol.DispatchPayload(envelope.PayloadJson), Convert.FromBase64String(envelope.SignatureBase64), HashAlgorithmName.SHA256))
                throw Denied();
            var claim = JsonSerializer.Deserialize<ComputeDispatchAuthorization>(envelope.PayloadJson, ComputeProtocol.Json) ?? throw Denied();
            var now = clock.GetUtcNow();
            if (claim.DispatchId == Guid.Empty || claim.OperationId == Guid.Empty || claim.EnvironmentId == Guid.Empty ||
                claim.InstallationId == Guid.Empty || claim.OrganizationId == Guid.Empty || claim.NodeId == Guid.Empty || claim.Generation < 1 ||
                claim.OrganizationId != enrollment.OrganizationId || claim.NodeId != enrollment.NodeId || claim.ProviderId != enrollment.ProviderId ||
                capabilities.ProviderId != enrollment.ProviderId || claim.IssuedAt > now.AddSeconds(5) || claim.ExpiresAt <= now ||
                claim.ExpiresAt <= claim.IssuedAt || claim.ExpiresAt - claim.IssuedAt > TimeSpan.FromMinutes(1) || !Enum.IsDefined(claim.Mode) ||
                claim.Action is not (InfrastructureActions.Provision or InfrastructureActions.Start or InfrastructureActions.Stop or
                    InfrastructureActions.Restart or InfrastructureActions.Destroy or InfrastructureActions.Execute or InfrastructureActions.PublishPort) || claim.Grants is null || claim.Grants.Count > 16 ||
                packet.Specification is not { IsValid: true } ||
                claim.ResourceId is { } resource && (resource.Length is < 1 or > 256 || resource.Any(char.IsControl))) throw Denied();
            var specificationJson = JsonSerializer.Serialize(packet.Specification, ComputeProtocol.Json);
            if (specificationJson.Length > 32768 || ComputeProtocol.Digest(specificationJson) != claim.SpecificationDigest) throw Denied();
            // Snapshot caller-owned lists before returning anything usable by an executor.
            var spec = JsonSerializer.Deserialize<ComputeSpecification>(specificationJson, ComputeProtocol.Json)!;
            ComputeTemplate? template = null;
            if (claim.Mode == ComputeDispatchMode.Observe)
            {
                if (claim.Grants.Count != 0 || packet.Template is not null || claim.TemplateDigest is not null || packet.Workload is not null || claim.WorkloadDigest is not null) throw Denied();
                return new(claim, spec, null);
            }
            ComputeWorkload? workload = null;
            if (claim.Action is InfrastructureActions.Execute or InfrastructureActions.PublishPort) {
                workload = packet.Workload?.Validate(spec.OperatingSystem) ?? throw Denied();
                if (claim.ResourceId is null || claim.EnvironmentLeaseExpiresAt <= now || claim.ExpiresAt > claim.EnvironmentLeaseExpiresAt ||
                    (workload.Command is not null) != (claim.Action == InfrastructureActions.Execute) || ComputeProtocol.Digest(JsonSerializer.Serialize(workload, ComputeProtocol.Json)) != claim.WorkloadDigest) throw Denied();
            } else if (packet.Workload is not null || claim.WorkloadDigest is not null) throw Denied();
            var energizing = claim.Action is InfrastructureActions.Provision or InfrastructureActions.Start or InfrastructureActions.Restart;
            var required = new HashSet<string>(StringComparer.Ordinal) { claim.Action };
            if (claim.Action == InfrastructureActions.PublishPort) required.Add(InfrastructureActions.Inbound);
            if (energizing)
            {
                if (claim.EnvironmentLeaseExpiresAt <= now || claim.ExpiresAt > claim.EnvironmentLeaseExpiresAt ||
                    (spec.LifetimeSeconds == 0 ? claim.EnvironmentLeaseExpiresAt != DateTimeOffset.MaxValue :
                        claim.EnvironmentLeaseExpiresAt - claim.IssuedAt > TimeSpan.FromSeconds(spec.LifetimeSeconds)) ||
                    !spec.Resources.Fits(maximumResources) || spec.Resources.GpuCount > 0 && !capabilities.SupportsGpu ||
                    spec.Persistence == ComputePersistence.Persistent && !capabilities.SupportsPersistentEnvironments ||
                    !capabilities.NetworkModes.Contains(spec.NetworkPolicy.Mode) || !capabilities.TemplateIds.Contains(spec.TemplateId)) throw Denied();
                if (spec.Persistence == ComputePersistence.Persistent) required.Add(InfrastructureActions.Persist);
                if (spec.NetworkPolicy.Mode == ComputeNetworkMode.Private) required.Add(InfrastructureActions.PrivateNetwork);
                if (spec.NetworkPolicy.AllowOutbound) required.Add(InfrastructureActions.Outbound);
                if (spec.NetworkPolicy.Mode == ComputeNetworkMode.Inbound) required.Add(InfrastructureActions.Inbound);
                if (spec.NetworkPolicy.PublicEndpoint) required.Add(InfrastructureActions.PublicEndpoint);
                if (spec.NetworkPolicy.Ports.Count > 0) required.Add(InfrastructureActions.PublishPort);
            }
            if (!capabilities.Actions.Contains(claim.Action) || claim.Grants.Count != required.Count ||
                claim.Grants.Any(x => x is null || x.GrantId == Guid.Empty || x.Revision < 1 || x.ExpiresAt < claim.ExpiresAt) ||
                energizing && spec.LifetimeSeconds == 0 && claim.Grants.Any(x => x.ExpiresAt != DateTimeOffset.MaxValue) ||
                !required.SetEquals(claim.Grants.Select(x => x.Action))) throw Denied();
            if (claim.Action == InfrastructureActions.Provision)
            {
                if (packet.Template is not { Features.Count: <= 64 } supplied || supplied.Features.Any(x => !ComputeSpecification.Identifier(x)) ||
                    !supplied.Matches(spec) || !certifiedTemplates.TryGetValue(spec.TemplateId, out var certified) || !certified.Enabled ||
                    certified.Features is null || !certified.Matches(spec) || certified.ImageDigest != supplied.ImageDigest || !certified.Features.SetEquals(supplied.Features)) throw Denied();
                var templateJson = JsonSerializer.Serialize(supplied, ComputeProtocol.Json);
                if (ComputeProtocol.Digest(templateJson) != claim.TemplateDigest) throw Denied();
                template = JsonSerializer.Deserialize<ComputeTemplate>(templateJson, ComputeProtocol.Json);
            }
            else if (packet.Template is not null || claim.TemplateDigest is not null || energizing && claim.ResourceId is null) throw Denied();
            return new(claim, spec, template, workload);
        }
        catch (Exception error) when (error is CryptographicException or FormatException or JsonException or NotSupportedException or ArgumentException)
        { throw new UnauthorizedAccessException("The compute dispatch signature or protocol is invalid.", error); }
    }

    private static UnauthorizedAccessException Denied() => new("The compute dispatch exceeds this provider's verified authority.");
}
