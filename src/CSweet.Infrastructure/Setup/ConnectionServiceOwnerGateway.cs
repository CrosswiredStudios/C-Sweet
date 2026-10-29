using System.Security.Cryptography;
using System.Text.Json;
using CSweet.ConnectionService.Protocol;
using CSweet.Contracts.Plugins;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

public sealed class ConnectionServiceOwnerOptions
{
    public const string Section = "CSweet:ConnectionServiceOwner";
    public string RegistrationPath { get; set; } = "";
}
internal sealed record ProtectedOwnerRegistration(OwnerRegistration Registration, string PrivateKey)
{ public override string ToString() => "Owner registration [redacted]"; }

public sealed class ConnectionServiceOwnerGateway(IHttpClientFactory clients, IDataProtectionProvider protection,
    IOptions<ConnectionServiceOwnerOptions> ownerOptions, ConnectionServiceSelectionStore selections)
{
    private readonly IDataProtector protector = protection.CreateProtector("CSweet.ConnectionService.Owner.v1");
    private string FilePath => Path.IsPathFullyQualified(ownerOptions.Value.RegistrationPath) ? ownerOptions.Value.RegistrationPath : throw new InvalidOperationException("Configure a separate protected owner registration path before owner setup.");
    public async Task<OwnerBootstrapDetails> Prepare(Guid actor, CancellationToken ct)
    {
        var choice = selections.Current();
        if (choice.Mode != "SelfHosted" || actor == Guid.Empty) throw new InvalidOperationException("Owner bootstrap is a separate action for your self-hosted service.");
        var origin = ManagedServiceTrust.CanonicalOrigin(choice.Origin!);
        ProtectedOwnerRegistration record;
        if (File.Exists(FilePath)) record = await Read(ct);
        else
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            record = new(new(Guid.NewGuid(), origin, Guid.NewGuid().ToString("N"), 1), DeploymentProofCodec.Encode(key.ExportPkcs8PrivateKey()));
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".pending";
            try
            {
                var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
                if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var stream = new FileStream(temporary, fileOptions))
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(protector.Protect(JsonSerializer.Serialize(record)));
                    await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct); stream.Flush(true);
                }
                ct.ThrowIfCancellationRequested();
                try { File.Move(temporary, FilePath, false); }
                catch (IOException) when (File.Exists(FilePath)) { record = await Read(ct); }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        if (record.Registration.ServiceOrigin != origin) throw new InvalidOperationException("Owner registration belongs to another service. Use local recovery, never retarget the key.");
        using var publicKey = ECDsa.Create(); publicKey.ImportPkcs8PrivateKey(DeploymentProofCodec.Decode(record.PrivateKey), out _);
        return new("bootstrap", origin, record.Registration.BindingId, DeploymentProofCodec.Encode(publicKey.ExportSubjectPublicKeyInfo()), record.Registration.KeyId,
            actor, "*", OwnerProtocol.Capabilities, 0);
    }
    public Task<OwnerAccess> Access(Guid actor, CancellationToken ct) => With(c => c.Access(actor, ct), ct);
    public Task<ProfileAvailability> Import(Guid actor, long revision, OwnerCredentialImport request, CancellationToken ct) => With(c => c.Import(actor, revision, request, ct), ct);
    public Task<OwnerAccepted> Approve(Guid actor, long revision, ApprovePackage request, CancellationToken ct) => With(c => c.Approve(actor, revision, request, ct), ct);
    public Task<DeploymentRegistration> Enroll(Guid actor, long revision, OwnerEnrollment request, CancellationToken ct) => With(c => c.Enroll(actor, revision, request, ct), ct);
    public Task<OwnerAccepted> Revoke(Guid actor, long revision, OwnerRevocation request, CancellationToken ct) => With(c => c.Revoke(actor, revision, request, ct), ct);
    private async Task<T> With<T>(Func<OwnerAdministrationClient, Task<T>> action, CancellationToken ct)
    {
        var record = await Read(ct); var choice = selections.Current();
        // Owner and customer roles remain distinct even when the owner also uses the official origin.
        if (choice.Mode == "Skip" || record.Registration.ServiceOrigin != ManagedServiceTrust.CanonicalOrigin(choice.Origin!)) throw new InvalidOperationException("Owner registration does not match the selected service.");
        using var key = ECDsa.Create(); key.ImportPkcs8PrivateKey(DeploymentProofCodec.Decode(record.PrivateKey), out _);
        using var http = clients.CreateClient(nameof(ConnectionServiceOwnerGateway));
        return await action(new OwnerAdministrationClient(http, record.Registration, key, TimeProvider.System));
    }
    private async Task<ProtectedOwnerRegistration> Read(CancellationToken ct)
    {
        if (!File.Exists(FilePath)) throw new InvalidOperationException("Owner bootstrap is required; customer pairing does not grant administration.");
        return JsonSerializer.Deserialize<ProtectedOwnerRegistration>(protector.Unprotect(await File.ReadAllTextAsync(FilePath, ct))) ?? throw new InvalidOperationException("Owner registration unavailable.");
    }
}
