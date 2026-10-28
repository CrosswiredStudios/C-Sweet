using System.Security.Cryptography;
using System.Text.Json;
using CSweet.ConnectionService.Protocol;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

public sealed record ManagedPairing(string ServiceOrigin, string PublicKey, string KeyId, string ReturnUri);
internal sealed record ManagedDeploymentRegistration(Guid DeploymentId, string TenantId, long Revision, string KeyId, string PrivateKey)
{ public override string ToString() => "Managed deployment registration [key redacted]"; }

/// <summary>Per-deployment proof keys, never provider credentials. Files are protected with this host's Data Protection key ring.</summary>
public interface IManagedPluginConnectionGateway
{
    bool UsesManaged(string profile);
    string ReturnUri { get; }
    Task<ProfileAvailability> Availability(string digest, CancellationToken ct);
    Task<ConnectionAttempt> Begin(BeginConnection request, CancellationToken ct);
    Task<ConnectionReceipt> Cancel(Guid session, CancellationToken ct);
    Task<ConnectionReceipt> CancelAttempt(Guid localAttemptId, CancellationToken ct);
    Task<ConnectionSnapshot> Supersede(Guid previous, Guid replacement, CancellationToken ct);
    Task<ConnectionReceipt> Redeem(RedeemConnection request, CancellationToken ct);
    Task<ConnectionReceipt> Recover(Guid session, RecoverConnection request, CancellationToken ct);
    Task<ConnectionInventory> Discover(Guid connection, CancellationToken ct);
    Task<ConnectionSnapshot> Disconnect(Guid connection, CancellationToken ct);
    Task<ConnectionSnapshot> Snapshot(Guid connection, CancellationToken ct);
}

public sealed class ManagedPluginConnectionGateway(IHttpClientFactory clients, IDataProtectionProvider protection,
    IOptions<PluginConnectionOptions> options) : IManagedPluginConnectionGateway
{
    private readonly IDataProtector protector = protection.CreateProtector("CSweet.ManagedConnection.Deployment.v1");
    private ManagedPluginConnectionOptions Settings => options.Value.Managed;
    public bool UsesManaged(string profile) => Settings.Enabled && !Settings.CustomerOwnedProfiles.Contains(profile, StringComparer.Ordinal);
    public string ReturnUri => Settings.ReturnUri;
    public async Task<CSweet.Contracts.Plugins.ManagedConnectionStatus> Status(CancellationToken ct)
    {
        if (!Settings.Enabled) return new(false, false, Settings.ServiceOrigin, Settings.ReturnUri);
        var paired = false;
        if (File.Exists(Settings.RegistrationPath)) paired = (await Read(ct)).DeploymentId != Guid.Empty;
        return new(true, paired, Settings.ServiceOrigin, Settings.ReturnUri) { CustomerOwnedProfiles = Settings.CustomerOwnedProfiles };
    }
    public Task<ProfileAvailability> Availability(string digest, CancellationToken ct) => With(c => c.Availability(digest, ct), ct);
    public Task<ConnectionAttempt> Begin(BeginConnection r, CancellationToken ct) => With(c => c.Begin(r, ct), ct);
    public Task<ConnectionReceipt> Cancel(Guid id, CancellationToken ct) => With(c => c.Cancel(id, ct), ct);
    public Task<ConnectionReceipt> CancelAttempt(Guid id, CancellationToken ct) => With(c => c.CancelAttempt(id, ct), ct);
    public Task<ConnectionSnapshot> Supersede(Guid previous, Guid replacement, CancellationToken ct) => With(c => c.Supersede(previous, replacement, ct), ct);
    public Task<ConnectionReceipt> Redeem(RedeemConnection r, CancellationToken ct) => With(c => c.Redeem(r, ct), ct);
    public Task<ConnectionReceipt> Recover(Guid id, RecoverConnection r, CancellationToken ct) => With(c => c.Recover(id, r, ct), ct);
    public Task<ConnectionInventory> Discover(Guid id, CancellationToken ct) => With(c => c.Discover(id, ct), ct);
    public Task<ConnectionSnapshot> Disconnect(Guid id, CancellationToken ct) => With(c => c.Disconnect(id, ct), ct);
    public Task<ConnectionSnapshot> Snapshot(Guid id, CancellationToken ct) => With(c => c.Get(id, ct), ct);
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
        var record = new ManagedDeploymentRegistration(Guid.Empty, "", 0, Guid.NewGuid().ToString("N"), DeploymentProofCodec.Encode(key.ExportPkcs8PrivateKey()));
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
    private async Task<T> With<T>(Func<ManagedConnectionClient, Task<T>> action, CancellationToken ct)
    {
        var record = await Read(ct);
        if (record.DeploymentId == Guid.Empty) throw new InvalidOperationException("managed_deployment_pairing_required");
        return await WithRecord(record, action);
    }
    private async Task<T> WithRecord<T>(ManagedDeploymentRegistration record, Func<ManagedConnectionClient, Task<T>> action)
    {
        using var key = ECDsa.Create(); key.ImportPkcs8PrivateKey(DeploymentProofCodec.Decode(record.PrivateKey), out _);
        using var http = clients.CreateClient(nameof(ManagedPluginConnectionGateway));
        return await action(new ManagedConnectionClient(http, new Uri(Settings.ServiceOrigin), record.DeploymentId, record.KeyId, record.Revision, key, TimeProvider.System));
    }
    private async Task<ManagedDeploymentRegistration> Read(CancellationToken ct)
    {
        Validate();
        if (!File.Exists(Settings.RegistrationPath)) throw new InvalidOperationException("managed_deployment_pairing_required");
        return JsonSerializer.Deserialize<ManagedDeploymentRegistration>(protector.Unprotect(await File.ReadAllTextAsync(Settings.RegistrationPath, ct)))
            ?? throw new InvalidOperationException("The deployment registration is unavailable.");
    }
    private void Validate()
    {
        if (!Settings.Enabled || !Uri.TryCreate(Settings.ServiceOrigin, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
            origin.AbsolutePath != "/" || !string.IsNullOrEmpty(origin.UserInfo) || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment) ||
            !Uri.TryCreate(Settings.ReturnUri, UriKind.Absolute, out var target) || target.AbsolutePath != "/settings/plugins/managed-return" ||
            !string.IsNullOrEmpty(target.UserInfo) || !string.IsNullOrEmpty(target.Query) || !string.IsNullOrEmpty(target.Fragment) ||
            !(target.Scheme == "https" || target.Scheme == "http" && target.IsLoopback) ||
            !Path.IsPathFullyQualified(Settings.RegistrationPath)) throw new InvalidOperationException("managed_deployment_configuration_required");
    }
}
