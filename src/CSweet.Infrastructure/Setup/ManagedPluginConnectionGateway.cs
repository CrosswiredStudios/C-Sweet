using System.Security.Cryptography;
using System.Text.Json;
using CSweet.ConnectionService.Protocol;
using CSweet.Contracts.Plugins;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

public sealed record ManagedPairing(string ServiceOrigin, string PublicKey, string KeyId, string ReturnUri);
public sealed record ManagedDeploymentAuthority(Guid DeploymentId, string TenantId, long Revision, string ServiceOrigin);
internal sealed record ManagedDeploymentRegistration(Guid DeploymentId, string TenantId, long Revision, string KeyId, string PrivateKey,
    string? ServiceOrigin = null)
{ public override string ToString() => "Managed deployment registration [key redacted]"; }

/// <summary>Per-deployment proof keys, never provider credentials. Files are protected with this host's Data Protection key ring.</summary>
public interface IManagedPluginConnectionGateway
{
    bool UsesManaged(string profile);
    string ReturnUri { get; }
    Task<string> BoundServiceOrigin(CancellationToken ct);
    Task<ManagedDeploymentAuthority> BoundAuthority(CancellationToken ct) => throw new NotSupportedException();
    Task<ProfileAvailability> Availability(string digest, CancellationToken ct);
    Task<ConnectionAttempt> Begin(BeginConnection request, CancellationToken ct, string? expectedServiceOrigin = null);
    Task<ConnectionReceipt> Cancel(Guid session, CancellationToken ct, string? expectedServiceOrigin = null);
    Task<ConnectionReceipt> CancelAttempt(Guid localAttemptId, CancellationToken ct, string? expectedServiceOrigin = null);
    Task<ConnectionSnapshot> Supersede(Guid previous, Guid replacement, CancellationToken ct, string? expectedServiceOrigin = null);
    Task<ConnectionReceipt> Redeem(RedeemConnection request, CancellationToken ct, string? expectedServiceOrigin = null);
    Task<ConnectionReceipt> Recover(Guid session, RecoverConnection request, CancellationToken ct, string? expectedServiceOrigin = null);
    Task<ConnectionInventory> Discover(Guid connection, CancellationToken ct, string? expectedServiceOrigin = null);
    Task<ConnectionSnapshot> Disconnect(Guid connection, CancellationToken ct, string? expectedServiceOrigin = null);
    Task<ConnectionSnapshot> Snapshot(Guid connection, CancellationToken ct, string? expectedServiceOrigin = null);
    Task<ConnectionAdoptionPreview> PreviewAdoption(Guid connection,ConnectionAdoptionRequest request,string origin,CancellationToken ct)=>throw new NotSupportedException();
    Task<ConnectionAdoptionReceipt> Adopt(Guid connection,ApproveConnectionAdoption request,string origin,CancellationToken ct)=>throw new NotSupportedException();
    Task<ConnectionAdoptionStatus> Adoption(Guid id,string origin,CancellationToken ct)=>throw new NotSupportedException();
    Task<ConnectionAdoptionReceipt> CancelAdoption(Guid connection,ConnectionAdoptionRequest request,string origin,CancellationToken ct)=>throw new NotSupportedException();
    Task<ManagedReadResult> ReadOperation(Guid connection, ManagedReadRequest request, string origin, CancellationToken ct) => throw new NotSupportedException();
    Task<ManagedMediaResult> BeginMedia(Guid connection, ManagedMediaRequest request, string origin, CancellationToken ct) => throw new NotSupportedException();
    Task<ManagedMediaResult> ProbeMedia(Guid transfer, string binding, string origin, CancellationToken ct) => throw new NotSupportedException();
    Task<ManagedMediaResult> MediaChunk(Guid transfer, string binding, long offset, ReadOnlyMemory<byte> chunk, string origin, CancellationToken ct) => throw new NotSupportedException();
    Task<ManagedMediaResult> CancelMedia(Guid transfer, string binding, string origin, CancellationToken ct) => throw new NotSupportedException();
}

public sealed class ManagedPluginConnectionGateway(IHttpClientFactory clients, IDataProtectionProvider protection,
    IOptions<PluginConnectionOptions> options, ConnectionServiceSelectionStore? selections = null) : IManagedPluginConnectionGateway
{
    private readonly IDataProtector protector = protection.CreateProtector("CSweet.ManagedConnection.Deployment.v1");
    private ManagedPluginConnectionOptions Settings => selections?.Effective(options.Value.Managed) ?? options.Value.Managed;
    public bool UsesManaged(string profile) => Settings.Enabled && !Settings.CustomerOwnedProfiles.Contains(profile, StringComparer.Ordinal);
    public string ReturnUri => Settings.ReturnUri;
    public async Task<CSweet.Contracts.Plugins.ManagedConnectionStatus> Status(CancellationToken ct)
    {
        string origin;
        try { origin = ManagedServiceTrust.CanonicalOrigin(Settings.ServiceOrigin); }
        catch (InvalidOperationException) { return new(Settings.Enabled, false, "", Settings.ReturnUri) { PairingState = "ConfigurationRequired" }; }
        var state = "NotPaired"; string? bound = null;
        if (File.Exists(Settings.RegistrationPath))
        {
            var record = await ReadStored(ct);
            if (record.ServiceOrigin is null) state = "LegacyUnbound";
            else
            {
                bound = ManagedServiceTrust.CanonicalOrigin(record.ServiceOrigin);
                state = bound != origin ? "OriginChanged" : record.DeploymentId == Guid.Empty ? "Prepared" : "Paired";
            }
        }
        return new(Settings.Enabled, state == "Paired", origin, Settings.ReturnUri)
        { CustomerOwnedProfiles = Settings.CustomerOwnedProfiles, PairingState = state, PairedServiceOrigin = bound };
    }
    public async Task<string> BoundServiceOrigin(CancellationToken ct)
    {
        var record = await Read(ct);
        if (record.DeploymentId == Guid.Empty) throw new InvalidOperationException("managed_deployment_pairing_required");
        return record.ServiceOrigin!;
    }
    public async Task<ManagedDeploymentAuthority> BoundAuthority(CancellationToken ct)
    {
        var record = await Read(ct);
        if (record.DeploymentId == Guid.Empty || string.IsNullOrWhiteSpace(record.TenantId) || record.Revision < 1)
            throw new InvalidOperationException("managed_deployment_pairing_required");
        return new(record.DeploymentId, record.TenantId, record.Revision, record.ServiceOrigin!);
    }
    public Task<ProfileAvailability> Availability(string digest, CancellationToken ct) => With(c => c.Availability(digest, ct), ct);
    public Task<ConnectionAttempt> Begin(BeginConnection r, CancellationToken ct, string? expectedServiceOrigin = null) => With(c => c.Begin(r, ct), ct, false, expectedServiceOrigin);
    public Task<ConnectionReceipt> Cancel(Guid id, CancellationToken ct, string? expectedServiceOrigin = null) => With(c => c.Cancel(id, ct), ct, true, expectedServiceOrigin);
    public Task<ConnectionReceipt> CancelAttempt(Guid id, CancellationToken ct, string? expectedServiceOrigin = null) => With(c => c.CancelAttempt(id, ct), ct, true, expectedServiceOrigin);
    public Task<ConnectionSnapshot> Supersede(Guid previous, Guid replacement, CancellationToken ct, string? expectedServiceOrigin = null) => With(c => c.Supersede(previous, replacement, ct), ct, true, expectedServiceOrigin);
    public Task<ConnectionReceipt> Redeem(RedeemConnection r, CancellationToken ct, string? expectedServiceOrigin = null) => With(c => c.Redeem(r, ct), ct, false, expectedServiceOrigin);
    public Task<ConnectionReceipt> Recover(Guid id, RecoverConnection r, CancellationToken ct, string? expectedServiceOrigin = null) => With(c => c.Recover(id, r, ct), ct, false, expectedServiceOrigin);
    public Task<ConnectionInventory> Discover(Guid id, CancellationToken ct, string? expectedServiceOrigin = null) => With(c => c.Discover(id, ct), ct, false, expectedServiceOrigin);
    public Task<ConnectionSnapshot> Disconnect(Guid id, CancellationToken ct, string? expectedServiceOrigin = null) => With(c => c.Disconnect(id, ct), ct, true, expectedServiceOrigin);
    public Task<ConnectionSnapshot> Snapshot(Guid id, CancellationToken ct, string? expectedServiceOrigin = null) => With(c => c.Get(id, ct), ct, true, expectedServiceOrigin);
    public Task<ConnectionAdoptionPreview> PreviewAdoption(Guid id,ConnectionAdoptionRequest r,string origin,CancellationToken ct)=>With(c=>c.PreviewAdoption(id,r,ct),ct,false,origin);
    public Task<ConnectionAdoptionReceipt> Adopt(Guid id,ApproveConnectionAdoption r,string origin,CancellationToken ct)=>With(c=>c.Adopt(id,r,ct),ct,false,origin);
    public Task<ConnectionAdoptionStatus> Adoption(Guid id,string origin,CancellationToken ct)=>With(c=>c.Adoption(id,ct),ct,true,origin);
    public Task<ConnectionAdoptionReceipt> CancelAdoption(Guid id,ConnectionAdoptionRequest r,string origin,CancellationToken ct)=>With(c=>c.CancelAdoption(id,r,ct),ct,true,origin);
    public Task<ManagedReadResult> ReadOperation(Guid id, ManagedReadRequest request, string origin, CancellationToken ct) => With(c => c.ReadOperation(id, request, ct), ct, false, origin);
    public Task<ManagedMediaResult> BeginMedia(Guid id, ManagedMediaRequest request, string origin, CancellationToken ct) => With(c => c.BeginMedia(id, request, ct), ct, false, origin);
    public Task<ManagedMediaResult> ProbeMedia(Guid id, string binding, string origin, CancellationToken ct) => With(c => c.ProbeMedia(id, binding, ct), ct, false, origin);
    public Task<ManagedMediaResult> MediaChunk(Guid id, string binding, long offset, ReadOnlyMemory<byte> chunk, string origin, CancellationToken ct) => With(c => c.MediaChunk(id, binding, offset, chunk, ct), ct, false, origin);
    public Task<ManagedMediaResult> CancelMedia(Guid id, string binding, string origin, CancellationToken ct) => With(c => c.CancelMedia(id, binding, ct), ct, true, origin);
    public async Task<ManagedPairing> PreparePairing(CancellationToken ct)
    {
        Validate();
        if (File.Exists(Settings.RegistrationPath))
        {
            var existing = await Read(ct);
            if (existing.DeploymentId != Guid.Empty) throw new InvalidOperationException("This deployment is already paired. Rotate its key explicitly instead of overwriting it.");
            using var stored = ECDsa.Create(); stored.ImportPkcs8PrivateKey(DeploymentProofCodec.Decode(existing.PrivateKey), out _);
            return new(Settings.ServiceOrigin, DeploymentProofCodec.Encode(stored.ExportSubjectPublicKeyInfo()), existing.KeyId, Settings.ReturnUri);
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var record = new ManagedDeploymentRegistration(Guid.Empty, "", 0, Guid.NewGuid().ToString("N"),
            DeploymentProofCodec.Encode(key.ExportPkcs8PrivateKey()), ManagedServiceTrust.CanonicalOrigin(Settings.ServiceOrigin));
        var parent = Path.GetDirectoryName(Path.GetFullPath(Settings.RegistrationPath))!; Directory.CreateDirectory(parent);
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(Settings.RegistrationPath, fileOptions);
        await using var writer = new StreamWriter(stream); await writer.WriteAsync(protector.Protect(JsonSerializer.Serialize(record)).AsMemory(), ct);
        return new(Settings.ServiceOrigin, DeploymentProofCodec.Encode(key.ExportSubjectPublicKeyInfo()), record.KeyId, Settings.ReturnUri);
    }
    public async Task CompletePairing(DeploymentRegistration supplied, CancellationToken ct)
    {
        if (supplied.DeploymentId == Guid.Empty || supplied.Revision < 1) throw new ArgumentException("A completed deployment registration is required.");
        var record = await Read(ct);
        if (record.DeploymentId != Guid.Empty) throw new InvalidOperationException("This deployment is already paired.");
        var candidate = record with { DeploymentId = supplied.DeploymentId, TenantId = supplied.TenantId, Revision = supplied.Revision };
        var confirmed = await WithRecord(candidate, c => c.Registration(ct));
        if (confirmed != supplied) throw new UnauthorizedAccessException("The service did not confirm this deployment binding.");
        var temporary = Settings.RegistrationPath + "." + Guid.NewGuid().ToString("N") + ".pending";
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using (var stream = new FileStream(temporary, fileOptions))
        await using (var writer = new StreamWriter(stream))
            await writer.WriteAsync(protector.Protect(JsonSerializer.Serialize(candidate)).AsMemory(), ct);
        File.Move(temporary, Settings.RegistrationPath, true);
    }
    private async Task<T> With<T>(Func<ManagedConnectionClient, Task<T>> action, CancellationToken ct, bool cleanup = false, string? expectedServiceOrigin = null)
    {
        // Disabling new managed connections does not erase the paired identity's cleanup duty.
        // All address, registration, protected-key and service proof checks still apply.
        var record = await Read(ct, cleanup);
        if (cleanup && expectedServiceOrigin is null || expectedServiceOrigin is not null &&
            record.ServiceOrigin != ManagedServiceTrust.CanonicalOrigin(expectedServiceOrigin))
            throw new InvalidOperationException("managed_connection_origin_mismatch");
        if (record.DeploymentId == Guid.Empty) throw new InvalidOperationException("managed_deployment_pairing_required");
        return await WithRecord(record, action);
    }
    private async Task<T> WithRecord<T>(ManagedDeploymentRegistration record, Func<ManagedConnectionClient, Task<T>> action)
    {
        using var key = ECDsa.Create(); key.ImportPkcs8PrivateKey(DeploymentProofCodec.Decode(record.PrivateKey), out _);
        using var http = clients.CreateClient(nameof(ManagedPluginConnectionGateway));
        return await action(new ManagedConnectionClient(http, new Uri(record.ServiceOrigin!), record.DeploymentId, record.KeyId, record.Revision, key, TimeProvider.System));
    }
    private async Task<ManagedDeploymentRegistration> Read(CancellationToken ct, bool cleanup = false)
    {
        if (!cleanup) Validate();
        var record = await ReadStored(ct);
        if (record.ServiceOrigin is null) throw new InvalidOperationException("managed_legacy_pairing_requires_recovery");
        var origin = ManagedServiceTrust.CanonicalOrigin(record.ServiceOrigin);
        if (record.ServiceOrigin != origin || !cleanup && origin != ManagedServiceTrust.CanonicalOrigin(Settings.ServiceOrigin))
            throw new InvalidOperationException("managed_service_origin_changed_repair_required");
        return record;
    }
    private async Task<ManagedDeploymentRegistration> ReadStored(CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(Settings.RegistrationPath)) throw new InvalidOperationException("managed_deployment_configuration_required");
        if (!File.Exists(Settings.RegistrationPath)) throw new InvalidOperationException("managed_deployment_pairing_required");
        return JsonSerializer.Deserialize<ManagedDeploymentRegistration>(protector.Unprotect(await File.ReadAllTextAsync(Settings.RegistrationPath, ct)))
            ?? throw new InvalidOperationException("The deployment registration is unavailable.");
    }
    private void Validate(bool requireEnabled = true)
    {
        _ = ManagedServiceTrust.CanonicalOrigin(Settings.ServiceOrigin);
        if (requireEnabled && !Settings.Enabled || !Uri.TryCreate(Settings.ServiceOrigin, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
            origin.AbsolutePath != "/" || !string.IsNullOrEmpty(origin.UserInfo) || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment) ||
            !Uri.TryCreate(Settings.ReturnUri, UriKind.Absolute, out var target) || target.AbsolutePath != "/settings/plugins/managed-return" ||
            !string.IsNullOrEmpty(target.UserInfo) || !string.IsNullOrEmpty(target.Query) || !string.IsNullOrEmpty(target.Fragment) ||
            !(target.Scheme == "https" || target.Scheme == "http" && target.IsLoopback) ||
            !Path.IsPathFullyQualified(Settings.RegistrationPath)) throw new InvalidOperationException("managed_deployment_configuration_required");
    }
}
