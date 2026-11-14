using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CSweet.HostedAccount.Tests;

/// <summary>Stand-in for the Market account issuer's HTTP surface.</summary>
internal sealed class FakeIssuer : HttpMessageHandler
{
    private readonly RsaSecurityKey _signingKey = new(RSA.Create(2048)) { KeyId = "k1" };
    private readonly RsaSecurityKey _otherKey = new(RSA.Create(2048)) { KeyId = "k1" };
    public string AdvertisedIssuer { get; init; } = HostedAccountClientTests.Issuer;
    public string VerificationUri { get; init; } = "https://account.test/device";
    public string? TokenError { get; set; }
    public HttpStatusCode RegistrationStatusOverride { get; set; }
    public int TokenCalls { get; private set; }
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;
    public string? IdTokenDefect { get; init; }
    public HttpStatusCode RegistrationStatus { get; set; } = HttpStatusCode.Created;
    public Guid InstallationId { get; } = Guid.NewGuid();
    public Dictionary<string, string>? DeviceForm { get; private set; }
    public string? RegistrationAuthorization { get; private set; }
    public JsonElement? RegistrationBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        switch (path)
        {
            case "/.well-known/openid-configuration":
                return Json(new Dictionary<string, string>
                {
                    ["issuer"] = AdvertisedIssuer,
                    ["device_authorization_endpoint"] = "https://account.test/connect/device",
                    ["token_endpoint"] = "https://account.test/connect/token",
                    ["jwks_uri"] = "https://account.test/.well-known/jwks"
                });
            case "/.well-known/jwks":
                var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(_signingKey);
                return Json(new { keys = new[] { new { kty = jwk.Kty, kid = jwk.Kid, n = jwk.N, e = jwk.E, use = "sig", alg = "RS256" } } });
            case "/connect/device":
                DeviceForm = (await request.Content!.ReadAsStringAsync(ct)).Split('&')
                    .Select(x => x.Split('=')).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1].Replace('+', ' ')));
                return Json(new
                {
                    device_code = "device-code", user_code = "WDJB-MJHT", verification_uri = VerificationUri,
                    verification_uri_complete = VerificationUri + "?user_code=WDJB-MJHT", expires_in = 900, interval = 5
                });
            case "/connect/token":
                TokenCalls++;
                if (TokenError is not null) return Json(new { error = TokenError }, HttpStatusCode.BadRequest);
                return Json(new { id_token = IdToken(), access_token = "access-token", token_type = "Bearer" });
            case "/api/installations":
                RegistrationAuthorization = request.Headers.Authorization?.ToString();
                RegistrationBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
                return RegistrationStatus == HttpStatusCode.Created
                    ? Json(new { installationId = InstallationId, displayName = "Northstar Headquarters", createdAt = DateTimeOffset.UtcNow }, HttpStatusCode.Created)
                    : Json(new { title = "conflict" }, RegistrationStatus);
            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private string IdToken()
    {
        var now = Now().UtcDateTime;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = "market-user-1",
            ["email"] = "maya@example.com",
            ["email_verified"] = IdTokenDefect != "unverified"
        };
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = IdTokenDefect == "issuer" ? "https://other.test/" : HostedAccountClientTests.Issuer,
            Audience = IdTokenDefect == "audience" ? "someone-else" : "csweet-hq",
            Claims = claims,
            IssuedAt = now.AddMinutes(-30),
            NotBefore = now.AddMinutes(-30),
            Expires = IdTokenDefect == "expired" ? now.AddMinutes(-10) : now.AddMinutes(10),
            SigningCredentials = new SigningCredentials(IdTokenDefect == "signature" ? _otherKey : _signingKey, SecurityAlgorithms.RsaSha256)
        });
    }

    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(value) };
}
