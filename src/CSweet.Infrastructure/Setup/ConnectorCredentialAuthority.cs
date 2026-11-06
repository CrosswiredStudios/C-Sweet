using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using CSweet.Application.Setup;
using CSweet.ConnectionService.Protocol;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Nonsecret authority identity, never a credential or a service execution permit.</summary>
public sealed record ManagedConnectorAuthoritySnapshot(string ServiceOrigin, Guid DeploymentId, string TenantId,
    long DeploymentRevision, Guid RemoteConnectionId, Guid RemoteSessionId, string GrantedScopeSetsHash,
    string ProfileId, long ProfileRevision, string ProfileBinding, Guid OriginalConsentId, long OriginalConsentRevision,
    Guid? AdoptionId, long? AdoptionRevision);

/// <summary>Choose a backend only from protected provenance. Never infer managed consent from a missing token.</summary>
public sealed class ConnectorCredentialAuthority(CSweetDbContext db, IPluginSecretStore secrets,
    IManagedPluginConnectionGateway? gateway = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public async Task<ManagedConnectorAuthoritySnapshot?> RequireAsync(Guid organization, AgentInstallation connector,
        PluginConnection connection, PluginManifest manifest, PluginConnectionDeclaration declaration,
        IReadOnlyList<string> requiredSets, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        string? value;
        try { value = await secrets.GetAsync(connector.Id, $"managed.context.{connection.Id:N}", deadline.Token); }
        catch (CryptographicException) { throw Denied(); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Denied(); }
        if (value is null)
        {
            if (gateway?.UsesManaged(connection.ProviderProfile) == true) throw Denied();
            return null; // Customer-owned credentials still require the existing local approval below.
        }
        try
        {
            if (gateway is null || !gateway.UsesManaged(connection.ProviderProfile) || value.Length > 16384 ||
                manifest.Accounts is null || connector.Scope != PluginInstallationScope.System) throw Denied();
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 8 });
            _ = ConnectorRequestMaterializer.Hash(document.RootElement); // Duplicate keys are never ambiguous authority.
            var context = document.RootElement.Deserialize<ManagedAccountAttempt>(Json) ?? throw Denied();
            if (context.RemoteConnectionId is not { } remote || remote == Guid.Empty || context.RemoteSessionId == Guid.Empty ||
                context.ServiceOrigin is null || ManagedServiceTrust.CanonicalOrigin(context.ServiceOrigin) != context.ServiceOrigin)
                throw Denied();
            var binding = await gateway.BoundAuthority(deadline.Token);
            if (binding.DeploymentId == Guid.Empty || binding.Revision < 1 || binding.TenantId != organization.ToString("D") ||
                binding.ServiceOrigin != context.ServiceOrigin || ManagedServiceTrust.CanonicalOrigin(binding.ServiceOrigin) != binding.ServiceOrigin)
                throw Denied();
            var original = await ManagedAccountProvenance.Original(db, connector.Id, connection.Id, deadline.Token);
            if (original.AccountGroupId != connection.AccountGroupId || original.ConnectionId != connection.Id || original.Status is not ("Ready" or "Archived") ||
                original.ProfileHash != connection.CredentialProfileHash || string.IsNullOrWhiteSpace(original.ProfileHash) ||
                original.PolicyVersion != manifest.Accounts.PolicyVersion || original.Revision < 1 ||
                await db.PluginAccountUpgrades.AsNoTracking().AnyAsync(x => x.ConnectionId == connection.Id && x.Status == "Pending", deadline.Token))
                throw Denied();
            PluginAccountUpgrade? adoption = null;
            if (original.PluginInstallationId == connector.Id)
            {
                if (original.PackageDigest != connector.PackageVersion!.PackageDigest) throw Denied();
            }
            else
            {
                var candidates = await db.PluginAccountUpgrades.AsNoTracking().Where(x => x.TargetInstallationId == connector.Id &&
                    x.ConnectionId == connection.Id && x.OriginalSetupSessionId == original.Id && x.Status == "Completed").Take(2).ToArrayAsync(deadline.Token);
                if (candidates.Length != 1) throw Denied();
                adoption = candidates[0];
                var request = JsonSerializer.Deserialize<ConnectionAdoptionRequest>(adoption.ServiceRequestJson, Json) ?? throw Denied();
                var sourceDigest = await db.AgentInstallations.AsNoTracking().Where(x => x.Id == adoption.SourceInstallationId)
                    .Select(x => x.PackageVersion!.PackageDigest).SingleOrDefaultAsync(deadline.Token);
                if (adoption.AccountGroupId != connection.AccountGroupId || adoption.ServiceOrigin != context.ServiceOrigin ||
                    adoption.RemoteConnectionId != remote || adoption.Revision < 1 || adoption.CompletedAt is null ||
                    connector.SupersedesInstallationId != adoption.SourceInstallationId || request.AdoptionId != adoption.Id ||
                    request.SourceInstallationId != adoption.SourceInstallationId || request.TargetInstallationId != connector.Id ||
                    request.SourcePackageDigest != sourceDigest || request.TargetPackageDigest != connector.PackageVersion!.PackageDigest ||
                    request.ExpectedProfileBinding != connection.CredentialProfileHash || request.OriginalConsentSessionId != context.RemoteSessionId ||
                    original.ApplicationUserId != adoption.ApplicationUserId || request.ActorId != adoption.ApplicationUserId.ToString("D")) throw Denied();
                var status = await gateway.Adoption(adoption.Id, context.ServiceOrigin, deadline.Token);
                var receipt = status.Receipt;
                if (!status.Found || receipt is null || receipt.AdoptionId != adoption.Id || receipt.ConnectionId != remote ||
                    receipt.RequestHash != CompatibleConnectionContract.Hash(JsonSerializer.SerializeToElement(request, Json)) ||
                    receipt.ReviewHash != adoption.ServiceReviewHash || receipt.CompatibilityHash != adoption.CompatibilityHash ||
                    receipt.Outcome != "Adopted" || request.ExpectedConnectionRevision == long.MaxValue ||
                    receipt.AdoptedRevision != request.ExpectedConnectionRevision + 1 || receipt.CurrentStatus != "Ready" ||
                    receipt.CurrentRevision < receipt.AdoptedRevision || receipt.CurrentInstallationId != connector.Id ||
                    receipt.CurrentPackageDigest != connector.PackageVersion.PackageDigest) throw Denied();
            }
            var profile = await gateway.Availability(connector.PackageVersion!.PackageDigest!, deadline.Token);
            if (profile.ProfileId != declaration.ProviderProfile || profile.PublicRevision < 1 ||
                profile.BindingHash != connection.CredentialProfileHash) throw Denied();
            var current = await gateway.Snapshot(remote, deadline.Token, context.ServiceOrigin);
            if (current.ConnectionId != remote || current.Status != "Ready" || current.Revision < 1 ||
                current.GrantedScopeSets.Count > 32 || current.GrantedScopeSets.Any(x => !declaration.ScopeSets.Any(s => s.Id == x)) ||
                requiredSets.Except(current.GrantedScopeSets, StringComparer.Ordinal).Any()) throw Denied();
            // Close a registration/mode switch during the bounded remote read sequence.
            if (!gateway.UsesManaged(connection.ProviderProfile) || await gateway.BoundAuthority(deadline.Token) != binding) throw Denied();
            return new(binding.ServiceOrigin, binding.DeploymentId, binding.TenantId, binding.Revision, remote, context.RemoteSessionId,
                ConnectorRequestMaterializer.Hash(JsonSerializer.SerializeToElement(current.GrantedScopeSets.Order(StringComparer.Ordinal).ToArray())),
                profile.ProfileId, profile.PublicRevision, profile.BindingHash, original.Id, original.Revision, adoption?.Id, adoption?.Revision);
        }
        catch (Exception error) when (error is JsonException or CryptographicException or InvalidOperationException or ArgumentException or HttpRequestException or NotSupportedException ||
            error is OperationCanceledException && !ct.IsCancellationRequested)
        { throw Denied(); }
    }

    private static UnauthorizedAccessException Denied() => new("The current managed connection authority is unavailable.");
}
