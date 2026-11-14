using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSweet.UI.Mobile;

/// <summary>Where the phone signs in (the C-Sweet account issuer) and how it identifies itself.</summary>
public sealed class MobileAccountOptions
{
    /// <summary>The account issuer. Production: <c>https://account.c-sweet.com/</c>.</summary>
    public Uri Issuer { get; init; } = new("https://account.c-sweet.com/");
    public string ClientId { get; init; } = "csweet-app";
    public Uri RedirectUri { get; init; } = new("com.csweet.app:/oauth/callback");
    public string Scope { get; init; } = "openid email profile offline_access directory";
    /// <summary>Development only: allow an <c>http://</c> issuer (the local Aspire Market).</summary>
    public bool AllowHttpIssuer { get; init; }

    public void Validate()
    {
        if (!Issuer.IsAbsoluteUri || !Issuer.AbsoluteUri.EndsWith('/'))
            throw new InvalidOperationException("The account issuer must be an absolute address ending with '/'.");
        if (Issuer.Scheme != Uri.UriSchemeHttps && !(AllowHttpIssuer && Issuer.Scheme == Uri.UriSchemeHttp))
            throw new InvalidOperationException("The account issuer must use HTTPS.");
    }
}

/// <summary>Result of the system browser step. <see cref="Parameters"/> is null when the person closed the browser.</summary>
public sealed record BrowserResult(IReadOnlyDictionary<string, string>? Parameters)
{
    public bool Cancelled => Parameters is null;
}

/// <summary>Opens the system authentication browser and returns the callback parameters (IL-07).</summary>
public interface IBrowserAuthenticator
{
    Task<BrowserResult> AuthenticateAsync(Uri start, Uri callback, CancellationToken ct);
}

/// <summary>Small protected key/value storage (Keychain / Android Keystore on devices).</summary>
public interface ISecureValueStore
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    Task RemoveAsync(string key);
}

public sealed class InMemorySecureValueStore : ISecureValueStore
{
    private readonly Dictionary<string, string> _values = new();
    public IReadOnlyDictionary<string, string> Values => _values;
    public Task<string?> GetAsync(string key) => Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
    public Task SetAsync(string key, string value) { _values[key] = value; return Task.CompletedTask; }
    public Task RemoveAsync(string key) { _values.Remove(key); return Task.CompletedTask; }
}

public sealed record AccountTokens(string Subject, string? Email, string AccessToken, DateTimeOffset AccessTokenExpiresAt, string? RefreshToken);

public enum SignInStatus { Succeeded, Cancelled, Failed }

public sealed record SignInOutcome(SignInStatus Status, AccountTokens? Tokens = null, string? Error = null);

/// <summary>The C-Sweet sign-in is no longer valid; the person must sign in again.</summary>
public sealed class AccountSessionExpiredException(string message = "Sign in to C-Sweet again.") : Exception(message);

/// <summary>A protocol or transport problem talking to the account issuer.</summary>
public sealed class AccountSignInException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// OpenID Connect authorization code + PKCE for the phone app, through the system browser. The ID token arrives
/// directly from the token endpoint over the issuer's TLS connection, so its issuer, audience, expiry and nonce are
/// checked here without a signature check (OpenID Connect Core 3.1.3.7).
/// </summary>
public sealed class AccountSignInClient(HttpClient http, MobileAccountOptions options, TimeProvider time)
{
    private Discovery? _discovery;
    private DateTimeOffset _discoveryExpires;

    public async Task<SignInOutcome> SignInAsync(IBrowserAuthenticator browser, bool createAccount, CancellationToken ct = default)
    {
        Discovery discovery;
        try
        {
            discovery = await DiscoverAsync(ct);
        }
        catch (Exception exception) when (!ct.IsCancellationRequested && IsTransient(exception))
        {
            return new SignInOutcome(SignInStatus.Failed, Error: "unreachable");
        }

        var verifier = RandomValue(32);
        var state = RandomValue(16);
        var nonce = RandomValue(16);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorize = new Uri(discovery.AuthorizationEndpoint + "?" + Query(
            ("client_id", options.ClientId), ("response_type", "code"), ("scope", options.Scope),
            ("redirect_uri", options.RedirectUri.OriginalString), ("state", state), ("nonce", nonce),
            ("code_challenge", challenge), ("code_challenge_method", "S256")));
        // Registration on the Market returns to the same authorization request after the email is verified.
        var start = createAccount
            ? new Uri(options.Issuer, "Account/Register?ReturnUrl=" + Uri.EscapeDataString(authorize.PathAndQuery))
            : authorize;

        var result = await browser.AuthenticateAsync(start, options.RedirectUri, ct);
        if (result.Cancelled) return new SignInOutcome(SignInStatus.Cancelled);
        var parameters = result.Parameters!;
        if (!parameters.TryGetValue("state", out var returnedState) || !FixedTimeEquals(returnedState, state))
            return new SignInOutcome(SignInStatus.Failed, Error: "state_mismatch");
        if (parameters.TryGetValue("error", out var error))
            return new SignInOutcome(SignInStatus.Failed, Error: error);
        if (!parameters.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
            return new SignInOutcome(SignInStatus.Failed, Error: "missing_code");

        try
        {
            var tokens = await RequestTokensAsync(discovery, nonce, previous: null, ct,
                ("grant_type", "authorization_code"), ("code", code), ("redirect_uri", options.RedirectUri.OriginalString),
                ("client_id", options.ClientId), ("code_verifier", verifier));
            return new SignInOutcome(SignInStatus.Succeeded, tokens);
        }
        catch (AccountSignInException exception)
        {
            return new SignInOutcome(SignInStatus.Failed, Error: exception.Code);
        }
        catch (AccountSessionExpiredException)
        {
            return new SignInOutcome(SignInStatus.Failed, Error: "invalid_grant");
        }
        catch (Exception exception) when (!ct.IsCancellationRequested && IsTransient(exception))
        {
            return new SignInOutcome(SignInStatus.Failed, Error: "unreachable");
        }
    }

    /// <summary>Refreshes the access token. Throws <see cref="AccountSessionExpiredException"/> when the issuer no longer accepts the refresh token.</summary>
    public async Task<AccountTokens> RefreshAsync(AccountTokens current, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(current.RefreshToken)) throw new AccountSessionExpiredException();
        var discovery = await DiscoverAsync(ct);
        return await RequestTokensAsync(discovery, expectedNonce: null, previous: current, ct,
            ("grant_type", "refresh_token"), ("refresh_token", current.RefreshToken), ("client_id", options.ClientId));
    }

    internal static bool IsTransient(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or AccountSignInException or JsonException;

    private async Task<AccountTokens> RequestTokensAsync(Discovery discovery, string? expectedNonce, AccountTokens? previous,
        CancellationToken ct, params (string Key, string Value)[] form)
    {
        using var response = await http.PostAsync(discovery.TokenEndpoint,
            new FormUrlEncodedContent(form.Select(x => new KeyValuePair<string, string>(x.Key, x.Value))), ct);
        var body = await ReadAsync<TokenResponse>(response, ct);
        if (!response.IsSuccessStatusCode || body is null || string.IsNullOrEmpty(body.AccessToken))
        {
            if (body?.Error == "invalid_grant") throw new AccountSessionExpiredException();
            throw new AccountSignInException(body?.Error ?? "token_failed", $"C-Sweet sign-in failed ({(int)response.StatusCode}).");
        }

        string subject;
        string? email;
        if (body.IdToken is { Length: > 0 } idToken)
        {
            var claims = ValidateIdToken(idToken, discovery.Issuer, expectedNonce);
            subject = claims.Subject;
            email = claims.Email ?? previous?.Email;
            if (previous is not null && previous.Subject != subject)
                throw new AccountSignInException("subject_changed", "C-Sweet returned a different account.");
        }
        else if (previous is not null)
        {
            subject = previous.Subject;
            email = previous.Email;
        }
        else
        {
            throw new AccountSignInException("missing_id_token", "C-Sweet did not identify the account.");
        }

        var lifetime = TimeSpan.FromSeconds(Math.Clamp(body.ExpiresIn ?? 300, 30, 86_400));
        return new AccountTokens(subject, email, body.AccessToken!, time.GetUtcNow() + lifetime, body.RefreshToken ?? previous?.RefreshToken);
    }

    private (string Subject, string? Email) ValidateIdToken(string idToken, string issuer, string? expectedNonce)
    {
        var parts = idToken.Split('.');
        if (parts.Length != 3) throw new AccountSignInException("invalid_id_token", "The sign-in response was malformed.");
        JsonElement payload;
        try
        {
            payload = JsonDocument.Parse(FromBase64Url(parts[1])).RootElement;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new AccountSignInException("invalid_id_token", "The sign-in response was malformed.");
        }

        if (String(payload, "iss") != issuer) throw new AccountSignInException("issuer_mismatch", "The sign-in came from an unexpected issuer.");
        var audienceOk = payload.TryGetProperty("aud", out var aud) && (aud.ValueKind == JsonValueKind.String
            ? aud.GetString() == options.ClientId
            : aud.ValueKind == JsonValueKind.Array && aud.EnumerateArray().Any(x => x.GetString() == options.ClientId));
        if (!audienceOk) throw new AccountSignInException("audience_mismatch", "The sign-in was issued to another application.");
        if (!payload.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var expires) ||
            DateTimeOffset.FromUnixTimeSeconds(expires) < time.GetUtcNow().AddMinutes(-1))
            throw new AccountSignInException("expired_id_token", "The sign-in response has expired.");
        if (expectedNonce is not null && !FixedTimeEquals(String(payload, "nonce") ?? "", expectedNonce))
            throw new AccountSignInException("nonce_mismatch", "The sign-in response does not match this request.");
        var subject = String(payload, "sub");
        if (string.IsNullOrEmpty(subject)) throw new AccountSignInException("invalid_id_token", "The sign-in did not name an account.");
        return (subject, String(payload, "email"));
    }

    private async Task<Discovery> DiscoverAsync(CancellationToken ct)
    {
        if (_discovery is not null && time.GetUtcNow() < _discoveryExpires) return _discovery;
        var document = await http.GetFromJsonAsync<DiscoveryDocument>(new Uri(options.Issuer, ".well-known/openid-configuration"), ct)
            ?? throw new AccountSignInException("discovery_failed", "C-Sweet sign-in is unavailable.");
        if (document.Issuer != options.Issuer.AbsoluteUri)
            throw new AccountSignInException("issuer_mismatch", "C-Sweet sign-in returned an unexpected issuer.");
        if (document.AuthorizationEndpoint is null || document.TokenEndpoint is null)
            throw new AccountSignInException("discovery_failed", "C-Sweet sign-in does not support the app.");
        var authorization = new Uri(document.AuthorizationEndpoint);
        var token = new Uri(document.TokenEndpoint);
        if (!SameOrigin(authorization) || !SameOrigin(token))
            throw new AccountSignInException("discovery_failed", "C-Sweet sign-in named an endpoint on another host.");
        _discovery = new Discovery(document.Issuer, authorization.GetLeftPart(UriPartial.Path), token);
        _discoveryExpires = time.GetUtcNow().AddMinutes(30);
        return _discovery;
    }

    private bool SameOrigin(Uri uri) =>
        uri.Scheme == options.Issuer.Scheme && string.Equals(uri.Host, options.Issuer.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == options.Issuer.Port;

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadFromJsonAsync<T>(ct); }
        catch (JsonException) { return default; }
        catch (NotSupportedException) { return default; }
    }

    private static string? String(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Query(params (string Key, string Value)[] values) =>
        string.Join('&', values.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string RandomValue(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    private sealed record Discovery(string Issuer, string AuthorizationEndpoint, Uri TokenEndpoint);

    private sealed record DiscoveryDocument(
        [property: JsonPropertyName("issuer")] string? Issuer,
        [property: JsonPropertyName("authorization_endpoint")] string? AuthorizationEndpoint,
        [property: JsonPropertyName("token_endpoint")] string? TokenEndpoint);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("id_token")] string? IdToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn,
        [property: JsonPropertyName("error")] string? Error);
}

/// <summary>The signed-in C-Sweet account on this phone, kept in protected storage and refreshed when needed.</summary>
public sealed class MobileAccountSession(AccountSignInClient client, ISecureValueStore store, TimeProvider time)
{
    internal const string StorageKey = "csweet.account.v1";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _refresh = new(1, 1);

    public AccountTokens? Current { get; private set; }

    public async Task<bool> RestoreAsync()
    {
        var json = await store.GetAsync(StorageKey);
        if (json is null) return false;
        try
        {
            Current = JsonSerializer.Deserialize<AccountTokens>(json);
        }
        catch (JsonException)
        {
            Current = null;
        }
        if (Current is null) await store.RemoveAsync(StorageKey);
        return Current is not null;
    }

    public async Task<SignInOutcome> SignInAsync(IBrowserAuthenticator browser, bool createAccount, CancellationToken ct = default)
    {
        var outcome = await client.SignInAsync(browser, createAccount, ct);
        if (outcome.Status == SignInStatus.Succeeded) await SaveAsync(outcome.Tokens!);
        return outcome;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (Current is null) throw new AccountSessionExpiredException();
        if (Current.AccessTokenExpiresAt - RefreshMargin > time.GetUtcNow()) return Current.AccessToken;
        await _refresh.WaitAsync(ct);
        try
        {
            if (Current is null) throw new AccountSessionExpiredException();
            if (Current.AccessTokenExpiresAt - RefreshMargin > time.GetUtcNow()) return Current.AccessToken;
            AccountTokens refreshed;
            try
            {
                refreshed = await client.RefreshAsync(Current, ct);
            }
            catch (AccountSessionExpiredException)
            {
                await SignOutAsync();
                throw;
            }
            await SaveAsync(refreshed);
            return refreshed.AccessToken;
        }
        finally
        {
            _refresh.Release();
        }
    }

    public async Task SignOutAsync()
    {
        Current = null;
        await store.RemoveAsync(StorageKey);
    }

    private async Task SaveAsync(AccountTokens tokens)
    {
        Current = tokens;
        await store.SetAsync(StorageKey, JsonSerializer.Serialize(tokens));
    }
}

public sealed record DirectoryEntry(Guid InstallationId, string DisplayName, string Role);

/// <summary>Reads the signed-in account's own Headquarters list from the C-Sweet directory (IL-06).</summary>
public sealed class HeadquartersDirectoryClient(HttpClient http, MobileAccountOptions options)
{
    /// <summary>Asks the directory for a relay admission ticket for <paramref name="phoneKey"/> (H4).</summary>
    public Task<ConnectionTicket> RequestTicketAsync(string accessToken, Guid installationId, string phoneKey, CancellationToken ct = default) =>
        DirectoryTicketClient.RequestTicketAsync(this, http, options, accessToken, installationId, phoneKey, ct);

    public async Task<IReadOnlyList<DirectoryEntry>> ListAsync(string accessToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.Issuer, "api/directory/installations"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new AccountSessionExpiredException();
        if (!response.IsSuccessStatusCode)
            throw new AccountSignInException("directory_failed", $"The Headquarters list is unavailable ({(int)response.StatusCode}).");
        var body = await response.Content.ReadFromJsonAsync<DirectoryResponse>(ct)
            ?? throw new AccountSignInException("directory_failed", "The Headquarters list was empty.");
        return body.Installations ?? [];
    }

    private sealed record DirectoryResponse(List<DirectoryEntry>? Installations);
}
