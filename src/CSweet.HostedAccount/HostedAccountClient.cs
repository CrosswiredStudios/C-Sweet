using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CSweet.HostedAccount;

public sealed record DeviceAuthorization(
    string DeviceCode,
    string UserCode,
    Uri VerificationUri,
    Uri VerificationUriComplete,
    Uri CreateAccountUri,
    DateTimeOffset ExpiresAt,
    TimeSpan PollInterval);

/// <summary>The C-Sweet account that approved the link, taken from a validated ID token.</summary>
public sealed record HostedIdentity(string Issuer, string Subject, string Email);

public enum DevicePollStatus { Pending, SlowDown, Approved, Denied, Expired, Failed }

public sealed record DevicePollResult(DevicePollStatus Status, HostedIdentity? Identity = null, string? AccessToken = null, string? Error = null);

public sealed record InstallationRegistration(Guid InstallationId, string DisplayName);

public sealed class HostedAccountException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Talks to the C-Sweet account issuer for hosted setup: device authorization, polling, ID token validation and
/// installation registration. Holds no state; callers keep the device code and key between calls.
/// </summary>
public sealed class HostedAccountClient(HttpClient http, IOptions<HostedAccountOptions> options, TimeProvider time)
{
    private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";
    private readonly SemaphoreSlim _discoveryLock = new(1, 1);
    private Discovery? _discovery;
    private DateTimeOffset _discoveryExpires;

    public async Task<DeviceAuthorization> StartAsync(string installationName, InstallationKey key, CancellationToken ct = default)
    {
        var discovery = await DiscoverAsync(ct);
        using var response = await http.PostAsync(discovery.DeviceAuthorizationEndpoint, Form(
            ("client_id", options.Value.ClientId),
            ("scope", "openid email profile installation"),
            ("installation_name", installationName),
            ("installation_key", key.PublicKey)), ct);
        var body = await ReadAsync<DeviceResponse>(response, ct);
        if (!response.IsSuccessStatusCode || body?.DeviceCode is null || body.UserCode is null || body.VerificationUri is null)
            throw new HostedAccountException(body?.Error ?? "device_authorization_failed", body?.ErrorDescription ?? "C-Sweet did not start the sign-in.");

        var verification = new Uri(body.VerificationUri, UriKind.Absolute);
        var complete = body.VerificationUriComplete is null
            ? new Uri(verification + "?user_code=" + Uri.EscapeDataString(body.UserCode))
            : new Uri(body.VerificationUriComplete, UriKind.Absolute);
        RequireIssuerHost(verification);
        RequireIssuerHost(complete);
        var returnUrl = complete.PathAndQuery;
        var createAccount = new Uri(options.Value.IssuerUri, "Account/Register?ReturnUrl=" + Uri.EscapeDataString(returnUrl));
        return new DeviceAuthorization(body.DeviceCode, body.UserCode, verification, complete, createAccount,
            time.GetUtcNow().AddSeconds(body.ExpiresIn is > 0 ? body.ExpiresIn.Value : 900),
            TimeSpan.FromSeconds(body.Interval is > 0 ? body.Interval.Value : 5));
    }

    public async Task<DevicePollResult> PollAsync(string deviceCode, CancellationToken ct = default)
    {
        var discovery = await DiscoverAsync(ct);
        using var response = await http.PostAsync(discovery.TokenEndpoint, Form(
            ("grant_type", DeviceGrant), ("device_code", deviceCode), ("client_id", options.Value.ClientId)), ct);
        var body = await ReadAsync<TokenResponse>(response, ct);
        if (body is null) return new(DevicePollStatus.Failed, Error: "C-Sweet returned an unreadable response.");
        switch (body.Error)
        {
            case "authorization_pending": return new(DevicePollStatus.Pending);
            case "slow_down": return new(DevicePollStatus.SlowDown);
            case "access_denied": return new(DevicePollStatus.Denied);
            case "expired_token": return new(DevicePollStatus.Expired);
            case not null: return new(DevicePollStatus.Failed, Error: body.ErrorDescription ?? body.Error);
        }
        if (body.IdToken is null || body.AccessToken is null)
            return new(DevicePollStatus.Failed, Error: "C-Sweet did not return the expected tokens.");
        var identity = await ValidateIdTokenAsync(body.IdToken, ct);
        return new(DevicePollStatus.Approved, identity, body.AccessToken);
    }

    public async Task<HostedIdentity> ValidateIdTokenAsync(string idToken, CancellationToken ct = default)
    {
        var discovery = await DiscoverAsync(ct);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = discovery.Issuer,
            ValidAudience = options.Value.ClientId,
            IssuerSigningKeys = discovery.SigningKeys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            LifetimeValidator = (notBefore, expires, _, _) =>
            {
                var now = time.GetUtcNow().UtcDateTime;
                return (notBefore is null || notBefore <= now.AddMinutes(2)) && expires is not null && expires > now.AddMinutes(-2);
            }
        });
        if (!result.IsValid)
            throw new HostedAccountException("invalid_id_token", "C-Sweet returned an identity that could not be verified.");
        var subject = result.Claims.TryGetValue("sub", out var sub) ? sub as string : null;
        var email = result.Claims.TryGetValue("email", out var mail) ? mail as string : null;
        var verified = result.Claims.TryGetValue("email_verified", out var flag) && flag is true or "true";
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email) || !verified)
            throw new HostedAccountException("invalid_id_token", "The C-Sweet account has no verified email address.");
        return new HostedIdentity(discovery.Issuer, subject, email);
    }

    /// <summary>Registers this installation. Safe to retry: the issuer returns the same installation for the same key.</summary>
    public async Task<InstallationRegistration> RegisterInstallationAsync(string accessToken, InstallationKey key, CancellationToken ct = default)
    {
        var timestamp = time.GetUtcNow().ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.Value.IssuerUri, "api/installations"))
        {
            Content = JsonContent.Create(new { publicKey = key.PublicKey, timestamp, signature = key.SignRegistration(timestamp, accessToken) })
        };
        request.Headers.Authorization = new("Bearer", accessToken);
        using var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            var body = await ReadAsync<RegistrationResponse>(response, ct);
            if (body is { InstallationId: var id } && id != Guid.Empty) return new InstallationRegistration(id, body.DisplayName ?? string.Empty);
        }
        throw new HostedAccountException(response.StatusCode == HttpStatusCode.Conflict ? "installation_conflict" : "registration_failed",
            "C-Sweet did not accept this installation.");
    }

    public async Task RetireInstallationAsync(Guid installationId, InstallationKey key, CancellationToken ct = default)
    {
        var timestamp = time.GetUtcNow().ToUnixTimeSeconds();
        using var response = await http.PostAsJsonAsync(new Uri(options.Value.IssuerUri, $"api/installations/{installationId:D}/retire"),
            new { timestamp, signature = key.SignRetirement(installationId, timestamp) }, ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            throw new HostedAccountException("retire_failed", "C-Sweet did not accept the request to disconnect.");
    }

    private async Task<Discovery> DiscoverAsync(CancellationToken ct)
    {
        if (_discovery is not null && time.GetUtcNow() < _discoveryExpires) return _discovery;
        await _discoveryLock.WaitAsync(ct);
        try
        {
            if (_discovery is not null && time.GetUtcNow() < _discoveryExpires) return _discovery;
            var issuer = options.Value.IssuerUri;
            var document = await http.GetFromJsonAsync<DiscoveryDocument>(new Uri(issuer, ".well-known/openid-configuration"), ct)
                ?? throw new HostedAccountException("discovery_failed", "C-Sweet sign-in is unavailable.");
            if (!string.Equals(document.Issuer, issuer.AbsoluteUri, StringComparison.Ordinal))
                throw new HostedAccountException("issuer_mismatch", "C-Sweet sign-in returned an unexpected issuer.");
            if (document.DeviceAuthorizationEndpoint is null || document.TokenEndpoint is null || document.JwksUri is null)
                throw new HostedAccountException("discovery_failed", "C-Sweet sign-in does not support Headquarters linking.");
            var device = new Uri(document.DeviceAuthorizationEndpoint);
            var token = new Uri(document.TokenEndpoint);
            var jwks = new Uri(document.JwksUri);
            RequireIssuerHost(device); RequireIssuerHost(token); RequireIssuerHost(jwks);
            var keys = new JsonWebKeySet(await http.GetStringAsync(jwks, ct));
            _discovery = new Discovery(document.Issuer, device, token, keys.GetSigningKeys());
            _discoveryExpires = time.GetUtcNow().AddMinutes(30);
            return _discovery;
        }
        finally { _discoveryLock.Release(); }
    }

    /// <summary>Never follow an endpoint to another host: every call stays on the configured issuer.</summary>
    private void RequireIssuerHost(Uri uri)
    {
        var issuer = options.Value.IssuerUri;
        if (!string.Equals(uri.Scheme, issuer.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Authority, issuer.Authority, StringComparison.OrdinalIgnoreCase))
            throw new HostedAccountException("issuer_mismatch", "C-Sweet sign-in pointed to an unexpected address.");
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(x => new KeyValuePair<string, string>(x.Key, x.Value)));

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadFromJsonAsync<T>(ct); }
        catch (JsonException) { return default; }
        catch (NotSupportedException) { return default; }
    }

    private sealed record Discovery(string Issuer, Uri DeviceAuthorizationEndpoint, Uri TokenEndpoint, IList<SecurityKey> SigningKeys);

    private sealed record DiscoveryDocument(
        [property: JsonPropertyName("issuer")] string Issuer,
        [property: JsonPropertyName("device_authorization_endpoint")] string? DeviceAuthorizationEndpoint,
        [property: JsonPropertyName("token_endpoint")] string? TokenEndpoint,
        [property: JsonPropertyName("jwks_uri")] string? JwksUri);

    private sealed record DeviceResponse(
        [property: JsonPropertyName("device_code")] string? DeviceCode,
        [property: JsonPropertyName("user_code")] string? UserCode,
        [property: JsonPropertyName("verification_uri")] string? VerificationUri,
        [property: JsonPropertyName("verification_uri_complete")] string? VerificationUriComplete,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn,
        [property: JsonPropertyName("interval")] int? Interval,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);

    private sealed record TokenResponse(
        [property: JsonPropertyName("id_token")] string? IdToken,
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);

    private sealed record RegistrationResponse(
        [property: JsonPropertyName("installationId")] Guid InstallationId,
        [property: JsonPropertyName("displayName")] string? DisplayName);
}
