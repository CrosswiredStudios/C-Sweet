using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Communications;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed class PluginSetupService(
    CSweetDbContext db,
    IPluginSecretStore secrets,
    IDataProtectionProvider protection,
    IHttpClientFactory httpClients,
    IPluginBootstrapCapabilityService bootstrap,
    IPluginProviderProfileRegistry providerProfiles,
    IAgentInstallationConfigurationService configurations,
    IAgentCommunicationOnboardingService onboarding,
    IAuditEventWriter audit) : IPluginSetupService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly IDataProtector _stateProtector = protection.CreateProtector("CSweet.PluginOAuth.State.v2");

    public async Task<PluginSetupResponse> GetAsync(Guid organizationId, Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var installation = await RequireInstallationAsync(organizationId, installationId, cancellationToken);
        return await MapAsync(installation, cancellationToken);
    }

    public async Task<PluginSetupResponse> CompleteStepAsync(Guid organizationId, Guid installationId, string stepId,
        CompletePluginSetupStepRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 160)
            throw new ArgumentException("A bounded idempotency key is required.");
        var installation = await RequireInstallationAsync(organizationId, installationId, cancellationToken);
        var manifest = Manifest(installation);
        var settingsFlow = installation.SetupState == PluginSetupState.Ready
            ? manifest.Ui.FirstOrDefault(x => x.Kind == "personal-settings")?.Flow : null;
        var flow = Flow(manifest, settingsFlow ?? installation.SetupFlowId);
        var step = flow.Steps.SingleOrDefault(x => x.Id == stepId)
            ?? throw new ArgumentException("The setup step is not declared by this plugin.");
        if (installation.SetupState != PluginSetupState.Ready && installation.SetupStepId != step.Id)
            throw new InvalidOperationException("Setup steps must be completed in order.");

        var data = SetupData.Parse(installation.SetupDataJson);
        if (step.Kind is "oauth-connect" or "permission-request")
        {
            var connection = await db.PluginConnections.AsNoTracking().SingleOrDefaultAsync(x =>
                x.AgentInstallationId == installation.Id && x.DeclarationId == step.Connection &&
                x.Status == PluginConnectionStatus.Connected, cancellationToken);
            var declaration = manifest.Connections.Single(x => x.Id == step.Connection);
            var scopeSet = declaration.ScopeSets.Single(x => x.Id == step.ScopeSet);
            var granted = DeserializeList(connection?.GrantedScopesJson);
            if (connection is null || scopeSet.Scopes.Except(granted, StringComparer.Ordinal).Any())
                throw new InvalidOperationException("The requested provider permissions have not been granted.");
        }
        else if (step.Kind == "account-selector")
        {
            var accountId = RequiredString(request.Values, "selectedAccountId", 256);
            var options = await bootstrap.InvokeAsync(organizationId, installationId, step.Id,
                JsonSerializer.SerializeToElement(new { }, JsonOptions), cancellationToken);
            var selected = ConnectorAccountProjection.Read(options, new ConnectorAccountOptions
            { ItemsPointer = "/accounts", IdPointer = "/id", NamePointer = "/name", HandlePointer = "/handle" })
                .SingleOrDefault(x => x.Id == accountId)
                ?? throw new InvalidOperationException("Choose an account returned by the connected provider.");
            var connection = await db.PluginConnections.SingleOrDefaultAsync(x =>
                x.AgentInstallationId == installation.Id && x.DeclarationId == step.Connection && x.Status == PluginConnectionStatus.Connected,
                cancellationToken) ?? throw new InvalidOperationException("Connect the provider before choosing an account.");
            if (connection.BoundResourceId is not null && connection.BoundResourceId != accountId)
                throw new InvalidOperationException("A connected installation cannot switch accounts without reconnecting.");
            connection.BoundResourceId = accountId;
            connection.ExternalAccountName = selected.Name;
            connection.UpdatedAt = DateTimeOffset.UtcNow;
            data.Values["selectedAccountId"] = JsonSerializer.SerializeToElement(selected.Id, JsonOptions);
            data.Values["selectedAccountName"] = JsonSerializer.SerializeToElement(selected.Name, JsonOptions);
        }
        else if (step.Kind == "form")
        {
            var current = await configurations.GetAsync(installation.Id, cancellationToken);
            var merged = (current?.Settings ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal))
                .ToDictionary(x => x.Key, x => x.Value.Clone(), StringComparer.Ordinal);
            foreach (var key in step.ConfigurationKeys)
                if (request.Values.TryGetValue(key, out var value))
                {
                    var declaration = manifest.Configuration.Single(x => x.Key == key);
                    if (declaration.Secret || declaration.Type.Equals("secret", StringComparison.OrdinalIgnoreCase))
                    {
                        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                            throw new InvalidOperationException($"Secure field '{key}' requires a non-empty value.");
                        await secrets.SetAsync(installation.Id, ConfigurationSecretKey(key), value.GetString()!, cancellationToken);
                        var reference = JsonSerializer.SerializeToElement(
                            $"vault://plugin/{installation.Id:N}/configuration/{Uri.EscapeDataString(key)}", JsonOptions);
                        data.Values[key] = reference;
                        merged[key] = reference;
                    }
                    else
                    {
                        data.Values[key] = value.Clone();
                        merged[key] = value.Clone();
                    }
                }
            await configurations.SaveAsync(installation.Id, current?.SchemaVersion ?? "1", merged, cancellationToken);
        }
        else if (step.Kind == "health-check")
        {
            var connection = await db.PluginConnections.AsNoTracking().SingleOrDefaultAsync(x =>
                x.AgentInstallationId == installation.Id && x.DeclarationId == step.Connection && x.Status == PluginConnectionStatus.Connected,
                cancellationToken);
            if (connection?.BoundResourceId is null) throw new InvalidOperationException("Connection validation failed.");
            var validation = await bootstrap.InvokeAsync(organizationId, installationId, step.Id,
                JsonSerializer.SerializeToElement(new { }, JsonOptions),
                cancellationToken);
            if (!validation.TryGetProperty("healthy", out var healthy) || healthy.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException(validation.TryGetProperty("message", out var message)
                    ? message.GetString() ?? "Connection validation failed." : "Connection validation failed.");
            data.Values[$"health:{step.Id}"] = validation.Clone();
        }

        if (installation.SetupState != PluginSetupState.Ready)
        {
            if (!data.CompletedStepIds.Contains(step.Id, StringComparer.Ordinal)) data.CompletedStepIds.Add(step.Id);
            var index = flow.Steps.IndexOf(step);
            installation.SetupStepId = index + 1 < flow.Steps.Count ? flow.Steps[index + 1].Id : null;
            installation.SetupDataJson = JsonSerializer.Serialize(data, JsonOptions);
        }
        installation.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("plugin-setup.step.completed", nameof(AgentInstallation), installation.Id,
            $"Completed safe setup step {step.Id}.", null, cancellationToken);
        return await MapAsync(installation, cancellationToken);
    }

    public async Task<BeginPluginAuthorizationResponse> BeginAuthorizationAsync(Guid organizationId,
        Guid applicationUserId, Guid installationId, string connectionId, BeginPluginAuthorizationRequest request,
        string redirectUri, CancellationToken cancellationToken = default)
    {
        var installation = await RequireInstallationAsync(organizationId, installationId, cancellationToken);
        await RequireConsentOwnerAsync(organizationId, applicationUserId, cancellationToken);
        var declaration = Manifest(installation).Connections.SingleOrDefault(x => x.Id == connectionId)
            ?? throw new ArgumentException("The connection is not declared by this plugin.");
        var scopeSet = declaration.ScopeSets.SingleOrDefault(x => x.Id == request.ScopeSetId)
            ?? throw new ArgumentException("The permission set is not declared by this plugin.");
        var profile = await providerProfiles.ResolveAsync(declaration.ProviderProfile, cancellationToken)
            ?? throw new InvalidOperationException("The administrator has not configured this verified provider profile.");
        await ValidateConnectorProfileAsync(installation, declaration, profile, cancellationToken);
        RequireConsentStep(installation, declaration.Id, scopeSet.Id);
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var redirect) || redirect.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(redirect.UserInfo) || !string.IsNullOrEmpty(redirect.Fragment) ||
            !string.IsNullOrEmpty(redirect.Query) || redirectUri.Length > 2048)
            throw new InvalidOperationException("OAuth callbacks require the configured HTTPS platform origin.");

        var attemptId = Guid.NewGuid();
        var nonce = Random(32);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var expires = DateTimeOffset.UtcNow.AddMinutes(10);
        var currentConnection = await db.PluginConnections.AsNoTracking().SingleOrDefaultAsync(x =>
            x.AgentInstallationId == installation.Id && x.DeclarationId == declaration.Id, cancellationToken);
        var declaredScopes = declaration.ScopeSets.SelectMany(x => x.Scopes).ToHashSet(StringComparer.Ordinal);
        var requestedScopes = scopeSet.Scopes.Concat(declaration.ScopeSets.Where(x => x.Required).SelectMany(x => x.Scopes))
            .Concat(currentConnection?.Status == PluginConnectionStatus.Connected
                ? DeserializeList(currentConnection.GrantedScopesJson).Where(declaredScopes.Contains) : [])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var payload = new OAuthState(attemptId, installation.Id, organizationId, applicationUserId, nonce, expires,
            declaration.Id, scopeSet.Id, redirectUri, requestedScopes,
            ConsentFingerprint(installation, profile, currentConnection));
        var state = _stateProtector.Protect(JsonSerializer.Serialize(payload, JsonOptions));
        db.PluginOAuthAttempts.Add(new PluginOAuthAttempt
        {
            Id = attemptId, AgentInstallationId = installation.Id, ApplicationUserId = applicationUserId,
            ConnectionDeclarationId = declaration.Id, ScopeSetId = scopeSet.Id,
            StateHash = Hash(state), RedirectUri = redirectUri, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = expires
        });
        await db.SaveChangesAsync(cancellationToken);
        await secrets.SetAsync(installation.Id, VerifierKey(attemptId), verifier, cancellationToken);

        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var parameters = new Dictionary<string, string>(declaration.Provider?.AuthorizationParameters
            ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var parameter in new Dictionary<string, string>
        {
            ["client_id"] = profile.ClientId, ["redirect_uri"] = redirectUri, ["response_type"] = "code",
            ["scope"] = string.Join(' ', requestedScopes), ["state"] = state,
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256"
        }) parameters.Add(parameter.Key, parameter.Value);
        var uri = Query(profile.AuthorizationEndpoint, parameters);
        return new BeginPluginAuthorizationResponse(uri, expires);
    }

    public async Task<PluginAuthorizationCompletion> CompleteAuthorizationAsync(Guid applicationUserId, string code, string state,
        CancellationToken cancellationToken = default)
    {
        OAuthState payload;
        try { payload = JsonSerializer.Deserialize<OAuthState>(_stateProtector.Unprotect(state), JsonOptions)!; }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        { throw new InvalidOperationException("OAuth state is invalid."); }
        if (payload is null || payload.ApplicationUserId != applicationUserId || payload.ExpiresAt <= DateTimeOffset.UtcNow ||
            payload.RequestedScopes is not { Length: > 0 } || string.IsNullOrEmpty(payload.AuthorityHash))
            throw new InvalidOperationException("OAuth state is expired or belongs to another user.");
        var attempt = await db.PluginOAuthAttempts.SingleOrDefaultAsync(x => x.Id == payload.AttemptId, cancellationToken)
            ?? throw new InvalidOperationException("OAuth attempt was not found.");
        if (attempt.AgentInstallationId != payload.InstallationId || attempt.ApplicationUserId != payload.ApplicationUserId ||
            attempt.ConnectionDeclarationId != payload.ConnectionId || attempt.ScopeSetId != payload.ScopeSetId ||
            attempt.RedirectUri != payload.RedirectUri || attempt.ConsumedAt.HasValue || attempt.ExpiresAt <= DateTimeOffset.UtcNow ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(attempt.StateHash), Convert.FromHexString(Hash(state))))
            throw new InvalidOperationException("OAuth state has expired or was already used.");
        attempt.ConsumedAt = DateTimeOffset.UtcNow;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { throw new InvalidOperationException("OAuth state was already used."); }
        var verifier = await secrets.GetAsync(attempt.AgentInstallationId, VerifierKey(attempt.Id), cancellationToken)
            ?? throw new InvalidOperationException("The PKCE verifier is unavailable.");
        // Consume the verifier even when later context validation fails.
        await secrets.RemoveAsync(attempt.AgentInstallationId, VerifierKey(attempt.Id), cancellationToken);

        var installation = await RequireInstallationAsync(payload.OrganizationId, payload.InstallationId, cancellationToken);
        var declaration = Manifest(installation).Connections.Single(x => x.Id == attempt.ConnectionDeclarationId);
        var scopeSet = declaration.ScopeSets.Single(x => x.Id == attempt.ScopeSetId);
        var profile = await providerProfiles.ResolveAsync(declaration.ProviderProfile, cancellationToken)
            ?? throw new InvalidOperationException("The administrator has not configured this verified provider profile.");
        await ValidateConnectorProfileAsync(installation, declaration, profile, cancellationToken);
        await RevalidateConsentAsync(payload, installation, profile, cancellationToken);
        using var response = await httpClients.CreateClient(nameof(PluginSetupService)).PostAsync(profile.TokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code, ["client_id"] = profile.ClientId, ["client_secret"] = profile.ClientSecret,
                ["redirect_uri"] = attempt.RedirectUri, ["grant_type"] = "authorization_code", ["code_verifier"] = verifier
            }), cancellationToken);
        var tokenJson = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("The provider rejected authorization.");
        using var token = JsonDocument.Parse(tokenJson, new JsonDocumentOptions { MaxDepth = 8 });
        _ = ConnectorRequestMaterializer.Canonical(token.RootElement); // Reject duplicate token/scope fields.
        if (token.RootElement.TryGetProperty("token_type", out var tokenType) &&
            (tokenType.ValueKind != JsonValueKind.String || !string.Equals(tokenType.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The provider returned an unsupported token type.");
        var accessToken = token.RootElement.GetProperty("access_token").GetString();
        var refreshToken = token.RootElement.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null;
        if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidOperationException("The provider did not return an access token.");
        var returnedScopes = token.RootElement.TryGetProperty("scope", out var scopeValue)
            ? (scopeValue.GetString() ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal)
            : payload.RequestedScopes.ToHashSet(StringComparer.Ordinal);
        if (payload.RequestedScopes.Except(returnedScopes, StringComparer.Ordinal).Any())
            throw new InvalidOperationException("The provider did not grant every requested permission.");
        // Providers may include previously consented scopes. That is not new host authority.
        returnedScopes.IntersectWith(payload.RequestedScopes);
        await RevalidateConsentAsync(payload, installation, profile, cancellationToken);

        var connection = await db.PluginConnections.SingleOrDefaultAsync(x =>
            x.AgentInstallationId == installation.Id && x.DeclarationId == declaration.Id, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (connection is null)
        {
            connection = new PluginConnection { Id = Guid.NewGuid(), AgentInstallationId = installation.Id,
                DeclarationId = declaration.Id, ProviderProfile = declaration.ProviderProfile, CreatedAt = now };
            db.PluginConnections.Add(connection);
        }
        connection.GrantedScopesJson = JsonSerializer.Serialize(returnedScopes.Order(StringComparer.Ordinal));
        connection.Status = PluginConnectionStatus.Connected;
        connection.UpdatedAt = now;
        connection.RevokedAt = null;
        var flow = Flow(Manifest(installation), installation.SetupFlowId);
        var oauthStep = flow.Steps.SingleOrDefault(x => x.Id == installation.SetupStepId &&
            x.Connection == declaration.Id && x.ScopeSet == scopeSet.Id &&
            (x.Kind is "oauth-connect" or "permission-request"));
        if (oauthStep is not null)
        {
            var setupData = SetupData.Parse(installation.SetupDataJson);
            if (!setupData.CompletedStepIds.Contains(oauthStep.Id, StringComparer.Ordinal))
                setupData.CompletedStepIds.Add(oauthStep.Id);
            var oauthIndex = flow.Steps.IndexOf(oauthStep);
            installation.SetupStepId = oauthIndex + 1 < flow.Steps.Count ? flow.Steps[oauthIndex + 1].Id : null;
            installation.SetupDataJson = JsonSerializer.Serialize(setupData, JsonOptions);
            installation.UpdatedAt = now;
        }
        if (installation.SetupState == PluginSetupState.ConnectionRequired)
        {
            installation.SetupState = PluginSetupState.NeedsSetup;
            installation.IsEnabled = true;
            if (installation.Schedule is not null) installation.Schedule.IsEnabled = true;
        }
        // A new consent response may represent a different human/provider principal. Never
        // combine its access token with a refresh token from an earlier authorization.
        ResetAccountValidation(installation, declaration.Id);
        if (installation.Grant is not null) installation.Grant.GrantRevision++;
        var consumers = await db.AgentCapabilityBindings.Where(x => x.ProviderInstallationId == installation.Id &&
            x.OrganizationId == payload.OrganizationId.ToString("D")).Select(x => x.RequesterInstallationId).Distinct().ToListAsync(cancellationToken);
        consumers.Add(installation.Id);
        var policies = await db.PluginStandingPolicies.Where(x => x.OrganizationId == payload.OrganizationId &&
            consumers.Contains(x.AgentInstallationId) &&
            x.Status == PluginStandingPolicyStatus.Approved).ToListAsync(cancellationToken);
        foreach (var policy in policies)
        {
            policy.Status = PluginStandingPolicyStatus.Revoked;
            policy.RevokedAt = now;
        }
        // Persist the external-work gate before replacing credential material.
        connection.Status = PluginConnectionStatus.ReauthorizationRequired;
        await db.SaveChangesAsync(cancellationToken);
        await secrets.SetAsync(installation.Id, TokenKey(connection.Id), JsonSerializer.Serialize(new
        {
            accessToken, refreshToken, tokenType = "Bearer",
            expiresAt = now.AddSeconds(token.RootElement.TryGetProperty("expires_in", out var expiry) ? expiry.GetInt32() : 3600)
        }, JsonOptions), cancellationToken);
        connection.Status = PluginConnectionStatus.Connected;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            throw new UnauthorizedAccessException("The connection changed while credentials were being stored. Reconnect to recover.");
        }
        await audit.WriteAsync("plugin-connection.authorized", nameof(PluginConnection), connection.Id,
            $"Authorized provider profile {connection.ProviderProfile} with declared permission set {scopeSet.Id}.", null, cancellationToken);
        return new(payload.OrganizationId, installation.Id);
    }

    public async Task<CompletePluginSetupResponse> ActivateAsync(Guid organizationId, Guid applicationUserId,
        Guid installationId, CancellationToken cancellationToken = default)
    {
        var installation = await RequireInstallationAsync(organizationId, installationId, cancellationToken);
        var manifest = Manifest(installation);
        // Activation validates the completed setup record, never the ongoing settings surface.
        var flow = Flow(manifest, installation.SetupFlowId);
        var data = SetupData.Parse(installation.SetupDataJson);
        var persistedConfiguration = await configurations.GetAsync(installation.Id, cancellationToken);
        foreach (var value in persistedConfiguration?.Settings ?? new Dictionary<string, JsonElement>())
            data.Values[value.Key] = value.Value.Clone();
        if (flow.Steps.Any(x => !data.CompletedStepIds.Contains(x.Id, StringComparer.Ordinal)))
            throw new InvalidOperationException("Complete every setup step before activation.");
        foreach (var declaration in manifest.Connections)
        {
            var connection = await db.PluginConnections.SingleOrDefaultAsync(x =>
                x.AgentInstallationId == installation.Id && x.DeclarationId == declaration.Id &&
                x.Status == PluginConnectionStatus.Connected, cancellationToken)
                ?? throw new InvalidOperationException("A required provider connection is missing.");
            var required = declaration.ScopeSets.Where(x => x.Required).SelectMany(x => x.Scopes).Distinct(StringComparer.Ordinal);
            if (required.Except(DeserializeList(connection.GrantedScopesJson), StringComparer.Ordinal).Any())
                throw new InvalidOperationException("Required provider permissions are missing.");
            if (flow.Steps.Any(x => x.Kind == "account-selector") && string.IsNullOrWhiteSpace(connection.BoundResourceId))
                throw new InvalidOperationException("Confirm the provider account before activation.");
        }
        await new ConnectorBindingService(db).ValidateRequiredBindingsAsync(installation, cancellationToken);
        if (installation.SetupState == PluginSetupState.Ready)
            return new(true, "Setup is already complete.", null);
        installation.SetupState = PluginSetupState.Ready;
        var setupObligation = await db.PluginSetupObligations.SingleOrDefaultAsync(x =>
            x.InstallationId == installation.Id && x.OrganizationId == organizationId, cancellationToken);
        if (setupObligation is not null) setupObligation.CompletedAt = DateTimeOffset.UtcNow;
        installation.SetupStepId = null;
        installation.IsEnabled = true;
        if (installation.Schedule is not null) installation.Schedule.IsEnabled = true;
        installation.UpdatedAt = DateTimeOffset.UtcNow;
        if (installation.PackageVersion!.PluginKind == PluginKind.Connector)
        {
            await db.SaveChangesAsync(cancellationToken);
            await audit.WriteAsync("plugin-setup.activated", nameof(AgentInstallation), installation.Id,
                "Connector setup validated; no employee or agent lifecycle event was created.", null, cancellationToken);
            return new(true, "The connection is ready.", null);
        }
        var agent = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == installation.Id && x.IsActive,
            cancellationToken) ?? throw new InvalidOperationException("The installed agent employee was not found.");
        var onboardingResult = await onboarding.EnsureAsync(
            organizationId,
            agent,
            applicationUserId,
            cancellationToken: cancellationToken);
        if (!onboardingResult.Succeeded) throw new InvalidOperationException(onboardingResult.Message);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("plugin-setup.activated", nameof(AgentInstallation), installation.Id,
            "Setup validation passed and the installation transitioned to Ready.", null, cancellationToken);
        return new(true, "The agent is connected and ready.", onboardingResult.ConversationId);
    }

    private async Task ValidateConnectorProfileAsync(AgentInstallation installation,
        PluginConnectionDeclaration declaration, PluginOAuthProviderProfile profile, CancellationToken token)
    {
        if (installation.PackageVersion!.PluginKind != PluginKind.Connector) return;
        var metadata = declaration.Provider ?? throw new InvalidOperationException("Provider metadata is required.");
        if (profile.AuthorizationEndpoint != metadata.AuthorizationEndpoint || profile.TokenEndpoint != metadata.TokenEndpoint ||
            profile.RevocationEndpoint != metadata.RevocationEndpoint ||
            !await db.ConnectorProfileApprovals.AnyAsync(x => x.ConnectorInstallationId == installation.Id &&
                x.PackageDigest == installation.PackageVersion.PackageDigest && x.ProfileId == profile.Id &&
                x.RevokedAt == null, token))
            throw new InvalidOperationException("An administrator must approve this exact connector build and OAuth profile.");
    }

    public async Task DisconnectAsync(Guid organizationId, Guid installationId, string connectionId,
        CancellationToken cancellationToken = default)
    {
        var installation = await RequireInstallationAsync(organizationId, installationId, cancellationToken);
        var connection = await db.PluginConnections.SingleOrDefaultAsync(x =>
            x.AgentInstallationId == installation.Id && x.DeclarationId == connectionId, cancellationToken);
        if (connection is null) return;
        // Persist the local deny before waiting on a remote revocation endpoint.
        connection.Status = PluginConnectionStatus.Revoked;
        connection.RevokedAt = connection.UpdatedAt = DateTimeOffset.UtcNow;
        installation.SetupState = PluginSetupState.ConnectionRequired;
        installation.IsEnabled = false;
        if (installation.Schedule is not null) installation.Schedule.IsEnabled = false;
        await db.SaveChangesAsync(cancellationToken);
        var profile = await providerProfiles.ResolveAsync(connection.ProviderProfile, cancellationToken);
        var tokenJson = await secrets.GetAsync(installation.Id, TokenKey(connection.Id), cancellationToken);
        if (profile is not null && !string.IsNullOrWhiteSpace(profile.RevocationEndpoint) && tokenJson is not null)
        {
            using var token = JsonDocument.Parse(tokenJson);
            var value = token.RootElement.TryGetProperty("refreshToken", out var refresh) ? refresh.GetString() :
                token.RootElement.GetProperty("accessToken").GetString();
            if (!string.IsNullOrWhiteSpace(value))
                try { await httpClients.CreateClient(nameof(PluginSetupService)).PostAsync(profile.RevocationEndpoint,
                    new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = value }), cancellationToken); }
                catch (HttpRequestException) { /* local disable and purge remain fail-closed */ }
        }
        await secrets.RemoveAsync(installation.Id, TokenKey(connection.Id), cancellationToken);
        foreach (var field in Manifest(installation).Configuration.Where(x =>
                     x.Secret || x.Type.Equals("secret", StringComparison.OrdinalIgnoreCase)))
            await secrets.RemoveAsync(installation.Id, ConfigurationSecretKey(field.Key), cancellationToken);
        connection.Status = PluginConnectionStatus.Revoked;
        connection.RevokedAt = connection.UpdatedAt = DateTimeOffset.UtcNow;
        connection.BoundResourceId = null;
        var manifest = Manifest(installation);
        var flow = Flow(manifest, manifest.Setup?.EntryFlow);
        var connectIndex = flow.Steps.ToList().FindIndex(x => x.Connection == connectionId && x.Kind == "oauth-connect");
        installation.SetupFlowId = flow.Id;
        installation.SetupStepId = connectIndex >= 0 ? flow.Steps[connectIndex].Id : flow.Steps.First().Id;
        var setupData = SetupData.Parse(installation.SetupDataJson);
        if (connectIndex >= 0)
        {
            var invalidated = flow.Steps.Skip(connectIndex).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            setupData.CompletedStepIds.RemoveAll(invalidated.Contains);
        }
        installation.SetupDataJson = JsonSerializer.Serialize(setupData, JsonOptions);
        installation.SetupState = PluginSetupState.ConnectionRequired;
        installation.IsEnabled = false;
        if (installation.Schedule is not null) installation.Schedule.IsEnabled = false;
        installation.UpdatedAt = DateTimeOffset.UtcNow;
        var configuration = await configurations.GetAsync(installation.Id, cancellationToken);
        if (configuration is not null)
        {
            var safeSettings = configuration.Settings.ToDictionary(x => x.Key, x => x.Value.Clone(), StringComparer.Ordinal);
            safeSettings["approvalMode"] = JsonSerializer.SerializeToElement("Manager Approval", JsonOptions);
            await configurations.SaveAsync(installation.Id, configuration.SchemaVersion, safeSettings, cancellationToken);
        }
        var operationalData = await db.PluginOperationalStates
            .Where(x => x.AgentInstallationId == installation.Id).ToListAsync(cancellationToken);
        db.PluginOperationalStates.RemoveRange(operationalData);
        var pendingActions = await db.ActionProposals.Where(x => x.AgentInstallationId == installation.Id &&
            x.Status == CSweet.Domain.Core.ProposalStatus.Pending).ToListAsync(cancellationToken);
        foreach (var proposal in pendingActions)
        {
            proposal.Status = CSweet.Domain.Core.ProposalStatus.Cancelled;
            proposal.DecidedAt = DateTimeOffset.UtcNow;
        }
        var standingPolicies = await db.PluginStandingPolicies.Where(x =>
            x.AgentInstallationId == installation.Id && x.Status == PluginStandingPolicyStatus.Approved)
            .ToListAsync(cancellationToken);
        foreach (var policy in standingPolicies)
        {
            policy.Status = PluginStandingPolicyStatus.Revoked;
            policy.RevokedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("plugin-connection.disconnected", nameof(PluginConnection), connection.Id,
            "Disabled external work, revoked authorization, and purged local token material.", null, cancellationToken);
    }

    private async Task<AgentInstallation> RequireInstallationAsync(Guid organizationId, Guid installationId,
        CancellationToken cancellationToken) => await db.AgentInstallations
        .Include(x => x.PackageVersion).Include(x => x.Schedule).Include(x => x.Grant)
        .SingleOrDefaultAsync(x => x.Id == installationId && x.BusinessId == organizationId.ToString("D"), cancellationToken)
        ?? throw new KeyNotFoundException("The plugin installation was not found in this organization.");

    private static PluginManifest Manifest(AgentInstallation installation) =>
        JsonSerializer.Deserialize<PluginManifest>(installation.PackageVersion!.ManifestJson, JsonOptions)
        ?? throw new InvalidOperationException("The installed plugin manifest is invalid.");

    private static PluginSetupFlow Flow(PluginManifest manifest, string? id) => manifest.Setup?.Flows
        .SingleOrDefault(x => x.Id == (id ?? manifest.Setup.EntryFlow))
        ?? throw new InvalidOperationException("The installed plugin has no setup flow.");

    private async Task<PluginSetupResponse> MapAsync(AgentInstallation installation, CancellationToken cancellationToken)
    {
        var manifest = Manifest(installation);
        var settingsFlow = installation.SetupState == PluginSetupState.Ready
            ? manifest.Ui.FirstOrDefault(x => x.Kind == "personal-settings")?.Flow : null;
        var flow = Flow(manifest, settingsFlow ?? installation.SetupFlowId);
        var data = SetupData.Parse(installation.SetupDataJson);
        var connections = await db.PluginConnections.AsNoTracking().Where(x => x.AgentInstallationId == installation.Id)
            .ToListAsync(cancellationToken);
        return new(installation.Id, installation.SetupState.ToString(), flow.Id, installation.SetupStepId,
            data.CompletedStepIds, flow, connections.Select(x => new PluginConnectionResponse(x.Id, x.DeclarationId,
                x.ProviderProfile, x.Status.ToString(), DeserializeList(x.GrantedScopesJson), x.ExternalAccountId,
                x.ExternalAccountName, x.BoundResourceId)).ToArray())
        {
            ConnectionDeclarations = manifest.Connections,
            ConfigurationFields = manifest.Configuration,
            Values = data.Values
        };
    }

    private static string RequiredString(IReadOnlyDictionary<string, JsonElement> values, string key, int max)
        => OptionalString(values, key, max) ?? throw new ArgumentException($"'{key}' is required.");
    private static string? OptionalString(IReadOnlyDictionary<string, JsonElement> values, string key, int max)
    {
        if (!values.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Length > max) return null;
        return text;
    }
    private static IReadOnlyList<string> DeserializeList(string? json) =>
        JsonSerializer.Deserialize<IReadOnlyList<string>>(json ?? "[]", JsonOptions) ?? [];
    private static string Random(int length) => Base64Url(RandomNumberGenerator.GetBytes(length));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string VerifierKey(Guid id) => $"oauth.attempt.{id:N}.verifier";
    private static string TokenKey(Guid id) => $"oauth.connection.{id:N}.token";
    public static string ConfigurationSecretKey(string fieldKey) => $"configuration.{fieldKey}.secret";
    private static string Query(string endpoint, IReadOnlyDictionary<string, string> values) =>
        endpoint + (endpoint.Contains('?') ? '&' : '?') + string.Join('&', values.Select(x =>
            $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));

    private sealed record OAuthState(Guid AttemptId, Guid InstallationId, Guid OrganizationId,
        Guid ApplicationUserId, string Nonce, DateTimeOffset ExpiresAt, string ConnectionId,
        string ScopeSetId, string RedirectUri, string[] RequestedScopes, string AuthorityHash);

    private async Task RequireConsentOwnerAsync(Guid organizationId, Guid applicationUserId, CancellationToken ct)
    {
        if (!await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.OrganizationId == organizationId &&
            x.ApplicationUserId == applicationUserId && x.IsActive && x.EmployeeType == CSweet.Domain.Core.EmployeeType.Human &&
            x.PermissionLevel >= CSweet.Domain.Core.OrganizationPermissionLevel.Manager, ct))
            throw new UnauthorizedAccessException("An active authorized human must complete provider consent.");
    }

    private static void RequireConsentStep(AgentInstallation installation, string connectionId, string scopeSetId)
    {
        if (installation.RevisionStatus != PluginRevisionStatus.Active ||
            (!installation.IsEnabled && installation.SetupState != PluginSetupState.ConnectionRequired))
            throw new UnauthorizedAccessException("The installation is not available for authorization.");
        var manifest = Manifest(installation);
        var settings = installation.SetupState == PluginSetupState.Ready
            ? manifest.Ui.FirstOrDefault(x => x.Kind == "personal-settings")?.Flow : null;
        var flow = Flow(manifest, settings ?? installation.SetupFlowId);
        if (!flow.Steps.Any(x => x.Connection == connectionId && x.ScopeSet == scopeSetId &&
            (x.Kind is "oauth-connect" or "permission-request") &&
            (installation.SetupState == PluginSetupState.Ready || x.Id == installation.SetupStepId)))
            throw new UnauthorizedAccessException("Consent must be initiated from the current declared setup or settings action.");
    }

    private static string ConsentFingerprint(AgentInstallation installation, PluginOAuthProviderProfile profile,
        PluginConnection? connection) => Hash(JsonSerializer.Serialize(new
        {
            installation.BusinessId, installation.PackageVersionId, installation.PackageVersion!.PackageDigest,
            manifestHash = Hash(installation.PackageVersion.ManifestJson), installation.RevisionStatus,
            installation.IsEnabled, installation.SetupState, installation.SetupFlowId, installation.SetupStepId,
            setupHash = Hash(installation.SetupDataJson), installation.Grant?.GrantRevision,
            profile.Id, profile.ClientId, profile.AuthorizationEndpoint, profile.TokenEndpoint, profile.RevocationEndpoint,
            connection = connection is null ? null : new
            { connection.Id, connection.Status, connection.BoundResourceId, connection.GrantedScopesJson, connection.UpdatedAt }
        }, JsonOptions));

    private async Task RevalidateConsentAsync(OAuthState payload, AgentInstallation installation,
        PluginOAuthProviderProfile originalProfile, CancellationToken ct)
    {
        await db.Entry(installation).ReloadAsync(ct);
        await db.Entry(installation.PackageVersion!).ReloadAsync(ct);
        if (installation.Grant is not null) await db.Entry(installation.Grant).ReloadAsync(ct);
        await RequireConsentOwnerAsync(payload.OrganizationId, payload.ApplicationUserId, ct);
        RequireConsentStep(installation, payload.ConnectionId, payload.ScopeSetId);
        var profile = await providerProfiles.ResolveAsync(originalProfile.Id, ct)
            ?? throw new UnauthorizedAccessException("The provider profile is no longer available.");
        await ValidateConnectorProfileAsync(installation,
            Manifest(installation).Connections.Single(x => x.Id == payload.ConnectionId), profile, ct);
        var connection = await db.PluginConnections.AsNoTracking().SingleOrDefaultAsync(x =>
            x.AgentInstallationId == installation.Id && x.DeclarationId == payload.ConnectionId, ct);
        if (installation.BusinessId != payload.OrganizationId.ToString("D") ||
            ConsentFingerprint(installation, profile, connection) != payload.AuthorityHash)
            throw new UnauthorizedAccessException("Authorization context changed. Start a new consent flow.");
    }

    private static void ResetAccountValidation(AgentInstallation installation, string connectionId)
    {
        var manifest = Manifest(installation);
        var flow = Flow(manifest, manifest.Setup?.EntryFlow);
        var index = flow.Steps.ToList().FindIndex(x => x.Connection == connectionId && x.Kind == "account-selector");
        if (index < 0) return;
        var data = SetupData.Parse(installation.SetupDataJson);
        var invalidated = flow.Steps.Skip(index).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        data.CompletedStepIds.RemoveAll(invalidated.Contains);
        foreach (var step in flow.Steps.Skip(index)) data.Values.Remove($"health:{step.Id}");
        installation.SetupState = PluginSetupState.NeedsSetup;
        installation.SetupFlowId = flow.Id;
        installation.SetupStepId = flow.Steps[index].Id;
        installation.SetupDataJson = JsonSerializer.Serialize(data, JsonOptions);
    }
    private sealed class SetupData
    {
        public List<string> CompletedStepIds { get; set; } = [];
        public Dictionary<string, JsonElement> Values { get; set; } = new(StringComparer.Ordinal);
        public static SetupData Parse(string json) => JsonSerializer.Deserialize<SetupData>(json, JsonOptions) ?? new();
    }
}

internal static class ReadOnlyListExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> values, T value)
    {
        for (var index = 0; index < values.Count; index++) if (EqualityComparer<T>.Default.Equals(values[index], value)) return index;
        return -1;
    }
}
