using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace CSweet.UI.Mobile;

/// <summary>A short-lived relay admission ticket from the C-Sweet directory, bound to this phone's key (H4).</summary>
public sealed record ConnectionTicket(string Ticket, string RelayOrigin, string RouteId, string HeadquartersPin, DateTimeOffset ExpiresAt, string? DeviceAssertion = null);

public enum HeadquartersConnectStatus { Connected, NotConfigured, NoAccess, Unreachable, SecureConnectionFailed, Refused }

/// <summary>
/// What a Connect achieved. When <see cref="Status"/> is Connected, <see cref="Enrollment"/> says whether this phone is
/// pending, active, denied, revoked, rejected or without access; for an active phone <see cref="Businesses"/> holds the
/// first live read.
/// </summary>
public sealed record HeadquartersConnectResult(
    HeadquartersConnectStatus Status,
    string? Protocol = null,
    string? HeadquartersName = null,
    string? Enrollment = null,
    IReadOnlyList<string>? Businesses = null);

/// <summary>Opens the relay channel and secure session to a Headquarters. Implemented by the native host.</summary>
public interface IHeadquartersConnector
{
    /// <summary>Connects to the Headquarters of <paramref name="installationId"/>. An active session is kept open for the enterprise app.</summary>
    Task<HeadquartersConnectResult> ConnectAsync(Guid installationId, ConnectionTicket ticket, ECDsa deviceKey, CancellationToken ct);
}

/// <summary>
/// Opens the full C-Sweet web app for one enterprise inside the phone app (TS-13 thin-shell prototype), over the
/// session the last Connect left open. The default host has no enterprise app.
/// </summary>
public interface IEnterpriseAppHost
{
    bool IsAvailable { get; }

    /// <summary>Opens the enterprise app; false when its session has ended and a new Connect is needed first.</summary>
    Task<bool> OpenAsync(Guid installationId, string displayName);
}

internal sealed class NoEnterpriseAppHost : IEnterpriseAppHost
{
    public bool IsAvailable => false;
    public Task<bool> OpenAsync(Guid installationId, string displayName) => Task.FromResult(false);
}

/// <summary>The directory refused a ticket (<c>relay_not_configured</c>, <c>not_member</c>, <c>invalid_phone_key</c>).</summary>
public sealed class DirectoryTicketException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// This phone's device key (ECDSA P-256): its relay admission key and its TLS client key. Created once per install and
/// kept in protected storage; never leaves the phone.
/// </summary>
public sealed class MobileDeviceKeyStore(ISecureValueStore store)
{
    internal const string StorageKey = "csweet.device-key.v1";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ECDsa? _key;

    public async Task<ECDsa> GetOrCreateAsync()
    {
        if (_key is not null) return _key;
        await _gate.WaitAsync();
        try
        {
            if (_key is not null) return _key;
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var stored = await store.GetAsync(StorageKey);
            try
            {
                if (stored is not null) key.ImportPkcs8PrivateKey(Convert.FromBase64String(stored), out _);
            }
            catch (Exception error) when (error is FormatException or CryptographicException)
            {
                stored = null;
                key.Dispose();
                key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            }
            if (stored is null) await store.SetAsync(StorageKey, Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
            return _key = key;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string PublicKey(ECDsa key) => AccountSignInClient.Base64Url(key.ExportSubjectPublicKeyInfo());

    /// <summary>The phone's short fingerprint, as Headquarters shows it on Settings → Phones.</summary>
    public static string Fingerprint(ECDsa key)
    {
        var pin = AccountSignInClient.Base64Url(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
        return string.Join(' ', Enumerable.Range(0, 4).Select(i => pin.Substring(i * 4, 4)));
    }
}

public static class DirectoryTicketClient
{
    public static async Task<ConnectionTicket> RequestTicketAsync(this HeadquartersDirectoryClient directory, HttpClient http, MobileAccountOptions options,
        string accessToken, Guid installationId, string phoneKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.Issuer, $"api/directory/installations/{installationId:D}/ticket"))
        {
            Content = JsonContent.Create(new { phoneKey })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new AccountSessionExpiredException();
        if (response.StatusCode == HttpStatusCode.NotFound) throw new DirectoryTicketException("not_member");
        if (!response.IsSuccessStatusCode)
        {
            string? code = null;
            try { code = (await response.Content.ReadFromJsonAsync<Problem>(ct))?.ErrorCode; } catch (System.Text.Json.JsonException) { }
            if (code is not null && response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest) throw new DirectoryTicketException(code);
            throw new AccountSignInException("ticket_failed", $"The connection ticket is unavailable ({(int)response.StatusCode}).");
        }
        return await response.Content.ReadFromJsonAsync<ConnectionTicket>(ct)
            ?? throw new AccountSignInException("ticket_failed", "The connection ticket was empty.");
    }

    private sealed record Problem(string? ErrorCode);
}
