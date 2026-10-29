using System.Security.Cryptography;
using System.Text.Json;
using CSweet.ConnectionService.Protocol;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

internal sealed record ManagedAccountAttempt(Guid RemoteSessionId, Guid? RemoteConnectionId, string ReturnVerifier, string? ServiceOrigin = null)
{ public override string ToString() => "Managed account attempt [proof redacted]"; }

public sealed partial class PluginAccountService
{
    private static string ManagedContextKey(Guid connection) => $"managed.context.{connection:N}";
    private async Task<BeginPluginAccountResponse> BeginManaged(Guid user, AgentInstallation installation, PluginManifest manifest,
        PluginConnectionDeclaration declaration, PluginConnection? previous, ConnectPluginAccountRequest request, string? browserBinding, CancellationToken ct)
    {
        if (browserBinding is null || browserBinding.Length != 43) throw new UnauthorizedAccessException("A browser-initiated connection is required.");
        var available = await managed!.Availability(request.PackageDigest, ct);
        var serviceOrigin = await managed.BoundServiceOrigin(ct);
        if (available.ProfileId != declaration.ProviderProfile) throw new UnauthorizedAccessException("The service profile differs from this package.");
        var selectable = manifest.Accounts!.Permissions.SelectMany(x => x.ScopeSets).Concat(declaration.ScopeSets.Where(x => x.Required).Select(x => x.Id)).ToHashSet(StringComparer.Ordinal);
        if (request.ScopeSetIds.Count > 32 || request.ScopeSetIds.Any(x => !selectable.Contains(x))) throw new ArgumentException("Only declared scope sets are available.");
        var requested = request.ScopeSetIds.Concat(declaration.ScopeSets.Where(x => x.Required).Select(x => x.Id))
            .Concat(previous is null ? [] : declaration.ScopeSets.Where(x => !x.Scopes.Except(List<string>(previous.GrantedScopesJson), StringComparer.Ordinal).Any()).Select(x => x.Id)).Distinct(StringComparer.Ordinal).ToArray();
        var group = previous?.AccountGroupId is { } groupId ? await db.PluginAccountGroups.SingleAsync(x => x.Id == groupId && x.PluginInstallationId == installation.Id, ct)
            : new PluginAccountGroup { Id = Guid.NewGuid(), PluginInstallationId = installation.Id, ProviderProfile = declaration.ProviderProfile,
                DisplayName = manifest.Accounts.AccountLabel, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        if (group.State is PluginAccountState.Disconnecting or PluginAccountState.Disconnected) throw new InvalidOperationException("Account disconnection must finish first.");
        if (previous is null) db.PluginAccountGroups.Add(group);
        var connection = new PluginConnection { Id = Guid.NewGuid(), AgentInstallationId = installation.Id, AccountGroupId = group.Id,
            DeclarationId = declaration.Id, ProviderProfile = declaration.ProviderProfile, CredentialProfileHash = available.BindingHash,
            Status = PluginConnectionStatus.Pending, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var session = new PluginAccountSetupSession { Id = Guid.NewGuid(), PluginInstallationId = installation.Id, ApplicationUserId = user,
            AccountGroupId = group.Id, ConnectionId = connection.Id, ReplacesConnectionId = previous?.Id,
            ReplacesAuthorizationRevision = previous?.AuthorizationRevision, PackageDigest = request.PackageDigest, ProfileHash = available.BindingHash,
            PolicyVersion = request.PolicyVersion, ScopeSetIdsJson = JsonSerializer.Serialize(requested), RedirectUri = managed.ReturnUri,
            StateHash = Hash(browserBinding), CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10), PolicyAcceptedAt = DateTimeOffset.UtcNow };
        var verifier = DeploymentProofCodec.Random();
        db.PluginConnections.Add(connection); db.PluginAccountSetupSessions.Add(session); await db.SaveChangesAsync(ct);
        // Persist before the external call so a crash never loses the local identity/verifier binding.
        await secrets.SetAsync(installation.Id, ManagedContextKey(connection.Id), JsonSerializer.Serialize(new ManagedAccountAttempt(Guid.Empty, null, verifier, serviceOrigin), Json), ct);
        var remote = await managed.Begin(new(session.Id, installation.Id, user.ToString("D"), request.PackageDigest, declaration.Id, requested,
            request.PolicyVersion, true, managed.ReturnUri, DeploymentProofCodec.Challenge(verifier)), ct, serviceOrigin);
        if (remote.LaunchUrl is null || remote.Status != "Created") throw new InvalidOperationException("The managed sign-in could not be started.");
        await secrets.SetAsync(installation.Id, ManagedContextKey(connection.Id), JsonSerializer.Serialize(new ManagedAccountAttempt(remote.SessionId, null, verifier, serviceOrigin), Json), ct);
        await audit.WriteAsync("plugin-account.managed-consent.started", nameof(PluginAccountSetupSession), session.Id,
            "A human initiated reviewed managed consent; provider credentials remain at the connection service.", cancellationToken: ct);
        return new(session.Id, remote.LaunchUrl, remote.ExpiresAt);
    }

    public async Task<ManagedPluginReturnResponse> CompleteManagedAsync(Guid user, Guid attemptId, string handoff, string? browserBinding, CancellationToken ct)
    {
        if (attemptId == Guid.Empty || string.IsNullOrEmpty(handoff) || handoff.Length != 43) throw new UnauthorizedAccessException("A bounded connection return is required.");
        var session = await db.PluginAccountSetupSessions.SingleOrDefaultAsync(x => x.Id == attemptId && x.ApplicationUserId == user, ct)
            ?? throw new UnauthorizedAccessException("This connection attempt belongs to another session.");
        if (browserBinding is null || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(session.StateHash), Convert.FromHexString(Hash(browserBinding))) ||
            session.ExpiresAt <= DateTimeOffset.UtcNow) throw new UnauthorizedAccessException("Return to the browser that started this connection.");
        var (_, _, declaration) = await Current(session, ct);
        if (managed?.UsesManaged(declaration.ProviderProfile) != true) throw new UnauthorizedAccessException("This attempt uses a different connection backend.");
        if (session.Status is "Importing" or "Ready" or "NoResources") return new(session.PluginInstallationId, session.Id);
        if (session.Status != "AwaitingConsent" || handoff.Length != 43) throw new UnauthorizedAccessException("This connection return is no longer usable.");
        var context = await ManagedAttempt(session, ct);
        var serviceOrigin = context.ServiceOrigin ?? throw new UnauthorizedAccessException("The connection service origin is unavailable.");
        ConnectionReceipt receipt;
        try { receipt = await managed.Redeem(new(handoff, context.ReturnVerifier, session.Id, user.ToString("D"), session.PackageDigest), ct, serviceOrigin); }
        catch (HttpRequestException)
        { receipt = await managed.Recover(context.RemoteSessionId, new(session.Id, user.ToString("D"), session.PackageDigest, context.ReturnVerifier), ct, serviceOrigin); }
        if (receipt.SessionId != context.RemoteSessionId) throw new UnauthorizedAccessException("The service returned a different attempt.");
        if (receipt.ConnectionId is not { } remoteId || receipt.Status != "AuthorizedPendingDiscovery")
        { session.Status = receipt.Status == "Cancelled" ? "Cancelled" : "Failed"; session.FailureCode = "managed_consent_not_completed"; session.Revision++; await db.SaveChangesAsync(ct); return new(session.PluginInstallationId, session.Id); }
        await Current(session, ct);
        var connection = await db.PluginConnections.SingleAsync(x => x.Id == session.ConnectionId, ct);
        var (_, manifest, _) = await Context(session.PluginInstallationId, ct);
        connection.GrantedScopesJson = JsonSerializer.Serialize(declaration.ScopeSets.Where(x => receipt.GrantedScopeSets.Contains(x.Id)).SelectMany(x => x.Scopes).Distinct(StringComparer.Ordinal));
        if (declaration.ScopeSets.Where(x => x.Required).Any(x => !receipt.GrantedScopeSets.Contains(x.Id))) throw new UnauthorizedAccessException("Required managed access is missing.");
        await secrets.SetAsync(session.PluginInstallationId, ManagedContextKey(connection.Id), JsonSerializer.Serialize(context with { RemoteConnectionId = remoteId }, Json), ct);
        connection.Status = PluginConnectionStatus.Connected; connection.AuthorizationRevision++; session.ConsumedAt = DateTimeOffset.UtcNow;
        session.Status = "Importing"; session.Revision++; await db.SaveChangesAsync(ct);
        return new(session.PluginInstallationId, session.Id);
    }

    private async Task<ManagedAccountAttempt> ManagedAttempt(PluginAccountSetupSession session, CancellationToken ct) =>
        JsonSerializer.Deserialize<ManagedAccountAttempt>(await secrets.GetAsync(session.PluginInstallationId, ManagedContextKey(session.ConnectionId), ct)
            ?? throw new InvalidOperationException("The managed attempt is unavailable."), Json)!;

    private async Task ImportManaged(PluginAccountSetupSession session, CancellationToken ct)
    {
        await Current(session, ct); var context = await ManagedAttempt(session, ct);
        if (context.RemoteConnectionId is not { } connectionId) throw new UnauthorizedAccessException("Managed consent has not completed.");
        var inventory = await managed!.Discover(connectionId, ct,
            context.ServiceOrigin ?? throw new UnauthorizedAccessException("The connection service origin is unavailable.")); await Current(session, ct);
        if (inventory.ConnectionId != connectionId || inventory.Status is not ("AuthorizedPendingDiscovery" or "Ready")) throw new UnauthorizedAccessException("Managed discovery is unavailable.");
        var (_, manifest, _) = await Context(session.PluginInstallationId, ct);
        if (inventory.Resources.Count > manifest.Accounts!.MaximumResources || inventory.Resources.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != inventory.Resources.Count)
            throw new InvalidOperationException("The managed inventory is invalid.");
        var rows = inventory.Resources.Select(x => new ConnectorAccountChoice(x.Id, x.Name, x.Handle)).ToArray();
        foreach (var row in rows)
            if (new[] { row.Id, row.Name, row.Handle }.Any(x => x is not null && (x.Length > 256 || x.Any(char.IsControl)))) throw new InvalidOperationException("Invalid managed resource label.");
        session.VerifiedSubject = inventory.Subject; session.VerifiedName = inventory.Name; session.IdentityAuthority = inventory.IdentityAuthority;
        session.ObservedResourcesJson = JsonSerializer.Serialize(rows, Json); session.PagesImported++; session.Revision++;
        if (inventory.Status == "Ready") await FinishImport(session, rows, ct);
        else { session.LeaseOwner = null; session.LeaseUntil = null; await db.SaveChangesAsync(ct); }
    }
    private async Task<bool> RetireManaged(PluginAccountSetupSession completed, CancellationToken ct)
    {
        if (managed is null || completed.ReplacesConnectionId is not { } previous) return false;
        try
        {
            var priorJson = await secrets.GetAsync(completed.PluginInstallationId, ManagedContextKey(previous), ct);
            if (priorJson is null) return true;
            var prior = JsonSerializer.Deserialize<ManagedAccountAttempt>(priorJson, Json)
                ?? throw new InvalidOperationException("The prior managed attempt is unavailable.");
            if (prior.ServiceOrigin is null) return false;
            var currentJson = await secrets.GetAsync(completed.PluginInstallationId, ManagedContextKey(completed.ConnectionId), ct);
            if (currentJson is not null)
            {
                var current = JsonSerializer.Deserialize<ManagedAccountAttempt>(currentJson, Json);
                if (prior.RemoteConnectionId is not { } oldRemote || current?.RemoteConnectionId is not { } newRemote) return false;
                if (current.ServiceOrigin != prior.ServiceOrigin)
                {
                    if (!await RevokeManaged(completed.PluginInstallationId, previous, prior, ct)) return false;
                }
                else
                {
                    var receipt = await managed.Supersede(oldRemote, newRemote, ct, prior.ServiceOrigin);
                    if (receipt.ConnectionId != oldRemote || receipt.Status != "Superseded") return false;
                }
            }
            else
            {
                // A customer-owned replacement has no central replacement proof. Revoke the old
                // managed grant through its original attempt rather than claiming a supersession.
                if (!await db.PluginConnections.AnyAsync(x => x.Id == completed.ConnectionId &&
                    x.AgentInstallationId == completed.PluginInstallationId && x.ActiveCredentialAttemptId != null, ct) ||
                    !await RevokeManaged(completed.PluginInstallationId, previous, prior, ct)) return false;
            }
            await db.TrackCopy(completed.AccountGroupId, null, completed.PluginInstallationId, "external:managed-service-backups", completed.AccountGroupId, ct);
            return true;
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or JsonException or
            UnauthorizedAccessException or CryptographicException || error is OperationCanceledException && !ct.IsCancellationRequested)
        { return false; }
    }

    private async Task<bool> RevokeManaged(Guid installation, Guid connection, ManagedAccountAttempt context, CancellationToken ct)
    {
        if (managed is null || context.ServiceOrigin is null) return false;
        var original = await db.PluginAccountSetupSessions.AsNoTracking().Where(x => x.PluginInstallationId == installation &&
            x.ConnectionId == connection && !x.IsMaintenance).OrderBy(x => x.CreatedAt).FirstAsync(ct);
        var cancelled = await managed.CancelAttempt(original.Id, ct, context.ServiceOrigin);
        if (cancelled.Status != "Cancelled" || context.RemoteSessionId != Guid.Empty && cancelled.SessionId != context.RemoteSessionId ||
            context.RemoteConnectionId is { } expected && cancelled.ConnectionId != expected) return false;
        if ((context.RemoteConnectionId ?? cancelled.ConnectionId) is not { } id) return true;
        var snapshot = await managed.Snapshot(id, ct, context.ServiceOrigin);
        return snapshot.ConnectionId == id && snapshot.Status == "Revoked";
    }
}
