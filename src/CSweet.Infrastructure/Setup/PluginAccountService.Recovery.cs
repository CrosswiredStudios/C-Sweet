using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed partial class PluginAccountService
{
    // This is eligibility for a human to request fresh consent, never a retry, access grant
    // or proof that erased credentials can be restored. The service validates retirement.
    private async Task<bool> CanRecoverUnverifiedManaged(Guid user, PluginAccountGroup group, PluginConnection connection,
        AgentInstallation installation, PluginConnectionDeclaration declaration, string? bindingHash, CancellationToken ct)
    {
        if (managed is null || bindingHash is null || group.State != PluginAccountState.ConnectionRequired || group.IdentityKind != PluginAccountIdentityKind.Administrative ||
            connection.Status != PluginConnectionStatus.Connected || group.VerifiedSubject is not null || connection.ExternalAccountId is not null ||
            await db.PluginAccountResources.AnyAsync(x => x.AccountGroupId == group.Id, ct) ||
            await db.PluginAccountBusinessGrants.AnyAsync(x => x.AccountGroupId == group.Id, ct)) return false;
        var attempt = await db.PluginAccountSetupSessions.AsNoTracking().Where(x => x.ConnectionId == connection.Id && !x.IsMaintenance)
            .OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (attempt is null || attempt.ApplicationUserId != user || attempt.PackageDigest != installation.PackageVersion!.PackageDigest ||
            attempt.ProfileHash != bindingHash || connection.CredentialProfileHash != bindingHash ||
            !List<string>(attempt.ScopeSetIdsJson).ToHashSet(StringComparer.Ordinal).SetEquals(declaration.ScopeSets
                .Where(x => !x.Scopes.Except(List<string>(connection.GrantedScopesJson), StringComparer.Ordinal).Any()).Select(x => x.Id)) ||
            attempt.Status != "ImportFailed" || attempt.VerifiedSubject is not null || attempt.PagesImported != 0 ||
            attempt.ObservedResourcesJson != "[]" || attempt.ExpiresAt.AddHours(1) > DateTimeOffset.UtcNow) return false;
        try
        {
            var context = await ManagedAttempt(attempt, ct);
            if (context.RemoteConnectionId is not { } id || context.ServiceOrigin is null || context.ServiceOrigin != await managed.BoundServiceOrigin(ct)) return false;
            var remote = await managed.Snapshot(id, ct, context.ServiceOrigin);
            return remote.ConnectionId == id && remote.Status == "Revoked" && remote.GrantedScopeSets.Count == 0;
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or JsonException or UnauthorizedAccessException or CryptographicException ||
            error is OperationCanceledException && !ct.IsCancellationRequested) { return false; }
    }
}
