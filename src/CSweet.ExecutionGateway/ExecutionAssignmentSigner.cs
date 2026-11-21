using System.Security.Cryptography;
using System.Text;
using CSweet.Office.Contracts.Security;
using CSweet.Application.Compute;
using CSweet.Compute.Contracts;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CSweet.ExecutionGateway;

public sealed class ExecutionAssignmentSigner : IDisposable, IComputeDispatchSigner
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public string KeyId { get; }

    public ExecutionAssignmentSigner(IOptions<ExecutionGatewayOptions> options, IHostEnvironment environment)
    {
        var configured = options.Value;
        var keyBytes = ResolvePrivateKey(configured, environment);
        _key.ImportPkcs8PrivateKey(keyBytes, out var read);
        if (read != keyBytes.Length)
            throw new CryptographicException("The assignment signing key contains trailing data.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(_key.ExportSubjectPublicKeyInfo()))
            .ToLowerInvariant();
        KeyId = string.IsNullOrWhiteSpace(configured.AssignmentSigningKeyId)
            ? $"ecdsa-p256-sha256:{fingerprint}"
            : configured.AssignmentSigningKeyId.Trim();
    }

    private static byte[] ResolvePrivateKey(ExecutionGatewayOptions options, IHostEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(options.AssignmentSigningPrivateKeyPkcs8Base64))
            return Convert.FromBase64String(options.AssignmentSigningPrivateKeyPkcs8Base64);
        if (!string.IsNullOrWhiteSpace(options.AssignmentSigningPrivateKeyPath))
            return File.ReadAllBytes(Path.GetFullPath(options.AssignmentSigningPrivateKeyPath));
        if (!environment.IsDevelopment())
            throw new InvalidOperationException(
                "A persistent execution-gateway assignment signing key is required outside development.");

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(string.IsNullOrWhiteSpace(local) ? AppContext.BaseDirectory : local,
            "CSweet", "Development", "execution-assignment-signing-key.pk8");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) return File.ReadAllBytes(path);
        using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bytes = generated.ExportPkcs8PrivateKey();
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".new";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
            stream.Write(bytes);
        try { File.Move(temporary, path); }
        catch (IOException) when (File.Exists(path)) { File.Delete(temporary); }
        return File.ReadAllBytes(path);
    }

    public byte[] Sign(
        Guid nodeId,
        Guid assignmentId,
        Guid workloadId,
        long epoch,
        string providerId,
        string specificationDigest,
        DateTimeOffset issuedAt,
        DateTimeOffset expiry) =>
        _key.SignData(AssignmentEnvelope.Payload(
            nodeId, assignmentId, workloadId, epoch, providerId, specificationDigest, issuedAt, expiry), HashAlgorithmName.SHA256);

    public string ExportPublicKeyBase64() => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    public byte[] SignComputeReceipt(Guid office, Guid operation, long sequence, string digest) =>
        _key.SignData(OfficeComputeReceiptEncoding.Payload(office, operation, sequence, digest), HashAlgorithmName.SHA256);

    public byte[] SignComputeMaintenanceReceipt(Guid office, Guid eventId, string digest) =>
        _key.SignData(OfficeComputeReceiptEncoding.MaintenancePayload(office, eventId, digest), HashAlgorithmName.SHA256);

    Task<SignedComputeDispatch> IComputeDispatchSigner.SignAsync(ComputeDispatchAuthorization authorization, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Serialize(authorization, ComputeProtocol.Json);
        return Task.FromResult(new SignedComputeDispatch(KeyId, payload,
            Convert.ToBase64String(_key.SignData(ComputeProtocol.DispatchPayload(payload), HashAlgorithmName.SHA256))));
    }

    Task<ComputeSigningIdentity> IComputeDispatchSigner.GetIdentityAsync(CancellationToken token) =>
        Task.FromResult(new ComputeSigningIdentity(KeyId, ExportPublicKeyBase64()));

    public void Dispose() => _key.Dispose();
}
