using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace CSweet.Infrastructure.Setup;

public sealed partial class PluginAccountService
{
    public async Task<BeginPluginAccountResponse> BeginAsync(Guid userId, Guid installationId, ConnectPluginAccountRequest request, CancellationToken ct, string? browserBinding = null)
    {
        var (installation, manifest, declaration) = await Context(installationId, ct);
        var previous = request.ReconnectConnectionId is { } oldId ? await db.PluginConnections.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == oldId && x.AgentInstallationId == installationId && x.AccountGroupId != null && x.Status != PluginConnectionStatus.Revoked, ct)
            ?? throw new UnauthorizedAccessException("The account authorization is unavailable.") : null;
        await Access.RequireOperatorAsync(userId, installationId, previous?.AccountGroupId, ct);
        if (managed?.UsesManaged(declaration.ProviderProfile) == true)
        {
            if (installation.PackageVersion!.Status != AgentPackageVersionStatus.Built || request.PackageDigest != installation.PackageVersion.PackageDigest ||
                !request.AcceptPolicies || request.PolicyVersion != manifest.Accounts!.PolicyVersion)
                throw new InvalidOperationException("Review the completed plugin and current connection terms first.");
            return await BeginManaged(userId, installation, manifest, declaration, previous, request, browserBinding, ct);
        }
        await using var consentTx=db.Database.IsRelational() && db.Database.CurrentTransaction==null ? await db.Database.BeginTransactionAsync(ct):null;
        if(db.Database.IsNpgsql()) _=await db.AgentInstallations.FromSqlInterpolated($"SELECT * FROM \"AgentInstallations\" WHERE \"Id\" = {installationId} FOR SHARE").AsNoTracking().ToArrayAsync(ct);
        (installation,manifest,declaration)=await Context(installationId,ct);
        if(manifest.Accounts!.Lifecycle.RevocationScope=="subject-project" && await db.PluginAccountRevocationJobs.AnyAsync(x=>
            x.PluginInstallationId==installationId && x.ProfileId==declaration.ProviderProfile && x.Status=="Pending",ct))
            throw new InvalidOperationException("Wait for the provider revocation to finish before starting a new authorization. It could invalidate the new connection.");
        if (installation.PackageVersion!.Status != AgentPackageVersionStatus.Built || string.IsNullOrWhiteSpace(request.PackageDigest) ||
            request.PackageDigest != installation.PackageVersion.PackageDigest) throw new InvalidOperationException("Review the current completed plugin build first.");
        if (!request.AcceptPolicies || request.PolicyVersion != manifest.Accounts!.PolicyVersion)
            throw new InvalidOperationException("Review and accept the current account connection terms first.");
        var profile = await profiles.ResolveAsync(declaration.ProviderProfile, ct) ?? throw new InvalidOperationException("provider_configuration_required");
        if (declaration.Provider is not { } provider || profile.AuthorizationEndpoint != provider.AuthorizationEndpoint || profile.TokenEndpoint != provider.TokenEndpoint ||
            profile.RevocationEndpoint != provider.RevocationEndpoint) throw new UnauthorizedAccessException("provider_configuration_mismatch");
        if (await Access.IsAdministratorAsync(userId, ct))
        {
            var approval = await db.ConnectorProfileApprovals.SingleOrDefaultAsync(x => x.ConnectorInstallationId == installationId && x.PackageDigest == request.PackageDigest && x.ProfileId == profile.Id, ct);
            if (approval is null) { approval = new() { Id = Guid.NewGuid(), ConnectorInstallationId = installationId, PackageDigest = request.PackageDigest, ProfileId = profile.Id }; db.ConnectorProfileApprovals.Add(approval); }
            approval.ProviderProfileHash = PluginAccountProfileGuard.Fingerprint(profile); approval.ApprovedAt = DateTimeOffset.UtcNow;
            approval.ApprovedByApplicationUserId = userId; approval.RevokedAt = null;
            await db.SaveChangesAsync(ct);
        }
        await PluginAccountProfileGuard.RequireAsync(db, profiles, installation, declaration, ct);
        var availableSets = manifest.Accounts.Permissions.SelectMany(x => x.ScopeSets).Concat(declaration.ScopeSets.Where(x => x.Required).Select(x => x.Id)).ToHashSet(StringComparer.Ordinal);
        if (request.ScopeSetIds is null || request.ScopeSetIds.Count > 32 || request.ScopeSetIds.Any(id => !availableSets.Contains(id)))
            throw new ArgumentException("Choose only permission sets declared by this plugin.");
        var sets = declaration.ScopeSets.Where(s => s.Required || request.ScopeSetIds.Contains(s.Id)).ToArray();
        var scopes = sets.SelectMany(s => s.Scopes).Concat(previous is null ? [] : List<string>(previous.GrantedScopesJson)
            .Intersect(declaration.ScopeSets.SelectMany(s => s.Scopes), StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (scopes.Length == 0) throw new InvalidOperationException("The plugin must request declared base permissions.");
        var redirect = PluginConnectionAddresses.Callback(options.Value);
        _ = PluginConnectionAddresses.Return(options.Value, "/settings/plugins", environment?.IsDevelopment() == true);
        var now = DateTimeOffset.UtcNow;
        var group = previous?.AccountGroupId is { } groupId ? await db.PluginAccountGroups.SingleAsync(x => x.Id == groupId && x.PluginInstallationId == installationId, ct) :
            new PluginAccountGroup { Id = Guid.NewGuid(), PluginInstallationId = installationId, DisplayName = manifest.Accounts.AccountLabel,
                ProviderProfile = profile.Id, CreatedAt = now, UpdatedAt = now };
        if (group.State is PluginAccountState.Disconnecting or PluginAccountState.Disconnected) throw new InvalidOperationException("Wait for account disconnection to finish before reconnecting.");
        if (previous is null) db.PluginAccountGroups.Add(group);
        var connection = new PluginConnection { Id = Guid.NewGuid(), AgentInstallationId = installationId, AccountGroupId = group.Id,
            DeclarationId = declaration.Id, ProviderProfile = profile.Id, Status = PluginConnectionStatus.Pending, CreatedAt = now, UpdatedAt = now };
        var session = new PluginAccountSetupSession { Id = Guid.NewGuid(), PluginInstallationId = installationId, ApplicationUserId = userId,
            AccountGroupId = group.Id, ConnectionId = connection.Id, ReplacesConnectionId = previous?.Id, PackageDigest = request.PackageDigest,
            ReplacesAuthorizationRevision = previous?.AuthorizationRevision,
            ProfileHash = PluginAccountProfileGuard.Fingerprint(profile), PolicyVersion = manifest.Accounts.PolicyVersion,
            ScopeSetIdsJson = JsonSerializer.Serialize(sets.Select(x => x.Id)), RequestedScopesJson = JsonSerializer.Serialize(scopes),
            RedirectUri = redirect, CreatedAt = now, ExpiresAt = now.AddMinutes(10), PolicyAcceptedAt = now };
        var state = "account." + _state.Protect(JsonSerializer.Serialize(new AccountState(session.Id, installationId, userId, session.ExpiresAt,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32))), Json));
        session.StateHash = Hash(state);
        var verifier = Base64(RandomNumberGenerator.GetBytes(32));
        db.PluginConnections.Add(connection); db.PluginAccountSetupSessions.Add(session);
        await db.SaveChangesAsync(ct);
        await secrets.SetAsync(installationId, VerifierKey(session.Id), verifier, ct);
        var parameters = new Dictionary<string, string>(provider.AuthorizationParameters, StringComparer.Ordinal);
        foreach (var pair in new Dictionary<string, string> { ["client_id"] = profile.ClientId, ["redirect_uri"] = redirect,
            ["response_type"] = "code", ["scope"] = string.Join(' ', scopes), ["state"] = state,
            ["code_challenge"] = Base64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), ["code_challenge_method"] = "S256" })
            parameters.Add(pair.Key, pair.Value);
        await audit.WriteAsync("plugin-account.consent.started", nameof(PluginAccountSetupSession), session.Id,
            "A human reviewed the current plugin, provider access and privacy terms and started account consent.", cancellationToken: ct);
        if(consentTx!=null) await consentTx.CommitAsync(ct);
        return new(session.Id, ConnectorRequestMaterializer.Query(profile.AuthorizationEndpoint, parameters), session.ExpiresAt);
    }

    public async Task<(Guid InstallationId, Guid SetupId)> CompleteAsync(Guid userId, string? code, string state, string? providerError, CancellationToken ct)
    {
        AccountState payload;
        try { payload = JsonSerializer.Deserialize<AccountState>(_state.Unprotect(state[8..]), Json)!; }
        catch (Exception e) when (e is CryptographicException or JsonException or ArgumentException)
        { throw new UnauthorizedAccessException("The account connection response is invalid."); }
        if (payload.UserId != userId || payload.ExpiresAt <= DateTimeOffset.UtcNow) throw new UnauthorizedAccessException("The account connection expired or belongs to another user.");
        var session = await db.PluginAccountSetupSessions.SingleOrDefaultAsync(x => x.Id == payload.SessionId && x.PluginInstallationId == payload.InstallationId &&
            x.ApplicationUserId == userId, ct) ?? throw new UnauthorizedAccessException("The account connection was not found.");
        if (session.Status != "AwaitingConsent" || session.ConsumedAt is not null || session.ExpiresAt <= DateTimeOffset.UtcNow ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(session.StateHash), Convert.FromHexString(Hash(state))))
            throw new InvalidOperationException("The account connection expired or was already used.");
        session.ConsumedAt = DateTimeOffset.UtcNow; session.Revision++;
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { throw new InvalidOperationException("The account response was already used."); }
        var verifier = await secrets.GetAsync(session.PluginInstallationId, VerifierKey(session.Id), ct);
        await secrets.RemoveAsync(session.PluginInstallationId, VerifierKey(session.Id), ct);
        if (!string.IsNullOrEmpty(providerError))
        { session.Status = "Cancelled"; await db.SaveChangesAsync(ct); return (session.PluginInstallationId, session.Id); }
        if (string.IsNullOrWhiteSpace(code) || verifier is null) throw new InvalidOperationException("The authorization response is incomplete. Connect again.");
        var (installation, manifest, declaration) = await Current(session, ct);
        var profile = await PluginAccountProfileGuard.RequireAsync(db, profiles, installation, declaration, ct);
        using var response = await http.CreateClient(nameof(PluginSetupService)).PostAsync(profile.TokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = profile.ClientId, ["client_secret"] = profile.ClientSecret,
                ["redirect_uri"] = session.RedirectUri, ["code"] = code, ["code_verifier"] = verifier, ["grant_type"] = "authorization_code" }), ct);
        if (!response.IsSuccessStatusCode) { await Fail(session, "provider_authorization_failed", ct); return (session.PluginInstallationId, session.Id); }
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct), new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement; _ = ConnectorRequestMaterializer.Canonical(root);
        var access = root.GetProperty("access_token").GetString();
        if (string.IsNullOrWhiteSpace(access) || access.Length > 8192 || root.TryGetProperty("token_type", out var type) &&
            (type.ValueKind != JsonValueKind.String || !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The provider returned an unsupported credential response.");
        var scopes = root.TryGetProperty("scope", out var scope) ? (scope.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries) : List<string>(session.RequestedScopesJson);
        scopes = scopes.Intersect(List<string>(session.RequestedScopesJson), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (declaration.ScopeSets.Where(x => x.Required).SelectMany(x => x.Scopes).Except(scopes, StringComparer.Ordinal).Any())
        { await Fail(session, "required_permissions_declined", ct); return (session.PluginInstallationId, session.Id); }
        var refresh = root.TryGetProperty("refresh_token", out var refreshNode) ? refreshNode.GetString() : null;
        var expiry = root.TryGetProperty("expires_in", out var expiresNode) ? expiresNode.GetInt32() : 3600;
        if (expiry is < 1 or > 86400 || refresh?.Length > 8192) throw new InvalidOperationException("The provider credential lifetime is invalid.");
        await Current(session, ct);
        var connection = await db.PluginConnections.SingleAsync(x => x.Id == session.ConnectionId && x.AgentInstallationId == session.PluginInstallationId, ct);
        if (connection.Status != PluginConnectionStatus.Pending) throw new UnauthorizedAccessException("The account changed during authorization.");
        await secrets.SetAsync(session.PluginInstallationId, TokenKey(session), JsonSerializer.Serialize(new
            { accessToken = access, refreshToken = refresh, tokenType = "Bearer", expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiry) }, Json), ct);
        connection.ActiveCredentialAttemptId = session.Id; connection.CredentialProfileHash = session.ProfileHash;
        connection.GrantedScopesJson = JsonSerializer.Serialize(scopes); connection.Status = PluginConnectionStatus.Connected; connection.UpdatedAt = DateTimeOffset.UtcNow;
        session.Status = "Importing"; session.Revision++;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { await secrets.RemoveAsync(session.PluginInstallationId, TokenKey(session), CancellationToken.None); throw new UnauthorizedAccessException("The account changed during credential storage."); }
        return (session.PluginInstallationId, session.Id);
    }

    private async Task<(AgentInstallation Installation, PluginManifest Manifest, PluginConnectionDeclaration Connection)> Current(PluginAccountSetupSession s, CancellationToken ct)
    {
        var context = await Context(s.PluginInstallationId, ct);
        if (!s.IsMaintenance)
        {
            await Access.RequireOperatorAsync(s.ApplicationUserId ?? throw new UnauthorizedAccessException(), s.PluginInstallationId, s.ReplacesConnectionId is null ? null : s.AccountGroupId, ct);
        }
        else if (!await db.PluginConnections.AsNoTracking().AnyAsync(x=>x.Id==s.ConnectionId && x.AccountGroupId==s.AccountGroupId &&
            x.Status==PluginConnectionStatus.Connected && x.AuthorizationRevision==s.ReplacesAuthorizationRevision,ct))
            throw new UnauthorizedAccessException("The maintenance authorization changed.");
        var managedMode = managed?.UsesManaged(context.Connection.ProviderProfile) == true;
        var managedJson = await secrets.GetAsync(s.PluginInstallationId, ManagedContextKey(s.ConnectionId), ct);
        if (managedJson is not null)
        {
            var origin = JsonSerializer.Deserialize<ManagedAccountAttempt>(managedJson, Json)?.ServiceOrigin;
            if (!managedMode || origin is null || origin != await managed!.BoundServiceOrigin(ct))
                throw new UnauthorizedAccessException("The connection service changed. Review a fresh connection.");
        }
        var bindingHash = managedMode
            ? (await managed!.Availability(s.PackageDigest, ct)).BindingHash
            : PluginAccountProfileGuard.Fingerprint(await PluginAccountProfileGuard.RequireAsync(db, profiles, context.Installation, context.Connection, ct));
        if (context.Installation.PackageVersion!.PackageDigest != s.PackageDigest || bindingHash != s.ProfileHash ||
            context.Manifest.Accounts!.PolicyVersion != s.PolicyVersion || !await db.PluginAccountGroups.AsNoTracking().AnyAsync(x => x.Id == s.AccountGroupId &&
                x.PluginInstallationId == s.PluginInstallationId && x.State != PluginAccountState.Disconnecting && x.State != PluginAccountState.Disconnected, ct))
            throw new UnauthorizedAccessException("The reviewed plugin or account authority changed. Connect again.");
        if (s.ReplacesConnectionId is { } previous && !await db.PluginConnections.AsNoTracking().AnyAsync(x => x.Id == previous &&
            x.AgentInstallationId == s.PluginInstallationId && x.AccountGroupId == s.AccountGroupId &&
            x.AuthorizationRevision == s.ReplacesAuthorizationRevision && x.Status != PluginConnectionStatus.Revoked, ct))
            throw new UnauthorizedAccessException("The previous authorization changed. Connect again.");
        return context;
    }
    private async Task Fail(PluginAccountSetupSession session, string reason, CancellationToken ct)
    { session.Status = "Failed"; session.FailureCode = reason; session.Revision++; await db.SaveChangesAsync(ct); }
    public async Task<(Guid InstallationId, Guid SetupId)?> FailCallbackAsync(Guid userId, string state, CancellationToken ct)
    {
        AccountState? payload;
        try { payload = JsonSerializer.Deserialize<AccountState>(_state.Unprotect(state[8..]), Json); }
        catch (Exception e) when (e is CryptographicException or JsonException or ArgumentException) { return null; }
        if (payload is null || payload.UserId != userId) return null;
        db.ChangeTracker.Clear();
        var s = await db.PluginAccountSetupSessions.SingleOrDefaultAsync(x => x.Id == payload.SessionId && x.PluginInstallationId == payload.InstallationId && x.ApplicationUserId == userId, ct);
        if (s is null || s.StateHash != Hash(state) || s.Status != "AwaitingConsent") return null;
        s.Status = s.ExpiresAt <= DateTimeOffset.UtcNow ? "Expired" : "Failed"; s.FailureCode = "authorization_failed";
        s.ConsumedAt ??= DateTimeOffset.UtcNow; s.Revision++;
        var connection = await db.PluginConnections.SingleOrDefaultAsync(x => x.Id == s.ConnectionId && x.Status == PluginConnectionStatus.Pending, ct);
        if (connection is not null) { connection.Status = PluginConnectionStatus.Revoked; connection.AuthorizationRevision++; }
        await db.SaveChangesAsync(ct); await secrets.RemoveAsync(s.PluginInstallationId, VerifierKey(s.Id), ct);
        return (s.PluginInstallationId, s.Id);
    }
    private static string Base64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private sealed record AccountState(Guid SessionId, Guid InstallationId, Guid UserId, DateTimeOffset ExpiresAt, string Nonce);
}
