using System.Security.Cryptography;
using System.Text;
using CSweet.SatelliteOffice.Contracts.Security;
using Microsoft.Extensions.Options;

namespace CSweet.ExecutionGateway;

public sealed class ExecutionAssignmentSigner : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public string KeyId { get; }

    public ExecutionAssignmentSigner(IOptions<ExecutionGatewayOptions> options, IHostEnvironment environment)
    {
        KeyId = options.Value.AssignmentSigningKeyId;
        if (!string.IsNullOrWhiteSpace(options.Value.AssignmentSigningPrivateKeyPkcs8Base64))
            _key.ImportPkcs8PrivateKey(
                Convert.FromBase64String(options.Value.AssignmentSigningPrivateKeyPkcs8Base64), out _);
        else if (!environment.IsDevelopment())
            throw new InvalidOperationException(
                "A shared execution-gateway assignment signing key is required outside development.");
    }

    public byte[] Sign(
        Guid nodeId,
        Guid assignmentId,
        long epoch,
        string specificationDigest,
        DateTimeOffset expiry,
        string artifactReadToken) =>
        _key.SignData(AssignmentEnvelope.Payload(
            nodeId, assignmentId, epoch, specificationDigest, expiry, artifactReadToken), HashAlgorithmName.SHA256);

    public string ExportPublicKeyBase64() => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    public void Dispose() => _key.Dispose();
}
