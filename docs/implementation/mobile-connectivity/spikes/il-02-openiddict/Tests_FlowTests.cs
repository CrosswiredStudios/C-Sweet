using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

public sealed class FlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public FlowTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private HttpClient Client(bool cookies = true) => _factory.CreateClient(new WebApplicationFactoryClientOptions
    { BaseAddress = new Uri("https://localhost"), HandleCookies = cookies, AllowAutoRedirect = false });

    private static async Task<JsonElement> Post(HttpClient c, string path, Dictionary<string, string> form)
    {
        var r = await c.PostAsync(path, new FormUrlEncodedContent(form));
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task Discovery_advertises_device_and_pkce()
    {
        var doc = JsonDocument.Parse(await Client(false).GetStringAsync("/.well-known/openid-configuration")).RootElement;
        Assert.True(doc.TryGetProperty("device_authorization_endpoint", out _));
        Assert.Contains("S256", doc.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Headquarters_device_flow_pending_then_approved_returns_id_token_for_the_market_user()
    {
        var hq = Client(false);
        var device = await Post(hq, "/connect/device", new() { ["client_id"] = "csweet-hq", ["scope"] = "openid email installation" });
        var deviceCode = device.GetProperty("device_code").GetString()!;
        var userCode = device.GetProperty("user_code").GetString()!;
        Assert.Contains(userCode.Replace("-", ""), Uri.UnescapeDataString(device.GetProperty("verification_uri_complete").GetString()!).Replace("-", ""));

        var poll = new Dictionary<string, string> { ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code", ["device_code"] = deviceCode, ["client_id"] = "csweet-hq" };
        Assert.Equal("authorization_pending", (await Post(hq, "/connect/token", poll)).GetProperty("error").GetString());

        var browser = Client();
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsync("/test/login?email=maya@example.com", null)).StatusCode);
        var approve = await browser.PostAsync("/connect/verify", new FormUrlEncodedContent(new Dictionary<string, string> { ["user_code"] = userCode, ["decision"] = "approve" }));
        Assert.True(approve.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK, approve.StatusCode.ToString());

        var tokens = await Post(hq, "/connect/token", poll);
        var idToken = tokens.GetProperty("id_token").GetString()!;
        var payload = JsonDocument.Parse(Base64Url(idToken.Split('.')[1])).RootElement;
        Assert.Equal("maya@example.com", payload.GetProperty("email").GetString());
        Assert.False(string.IsNullOrEmpty(payload.GetProperty("sub").GetString()));

        // Device code is single use.
        Assert.True((await Post(hq, "/connect/token", poll)).TryGetProperty("error", out _));
        // Ordinary cookie sign-in still works alongside the issuer.
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/test/whoami")).StatusCode);
    }

    [Fact]
    public async Task Denied_device_approval_reports_access_denied()
    {
        var hq = Client(false);
        var device = await Post(hq, "/connect/device", new() { ["client_id"] = "csweet-hq", ["scope"] = "openid email" });
        var browser = Client();
        await browser.PostAsync("/test/login?email=maya@example.com", null);
        await browser.PostAsync("/connect/verify", new FormUrlEncodedContent(new Dictionary<string, string> { ["user_code"] = device.GetProperty("user_code").GetString()!, ["decision"] = "deny" }));
        var tokens = await Post(hq, "/connect/token", new() { ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code", ["device_code"] = device.GetProperty("device_code").GetString()!, ["client_id"] = "csweet-hq" });
        Assert.Equal("access_denied", tokens.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Unknown_user_code_is_rejected()
    {
        var browser = Client();
        await browser.PostAsync("/test/login?email=maya@example.com", null);
        var r = await browser.PostAsync("/connect/verify", new FormUrlEncodedContent(new Dictionary<string, string> { ["user_code"] = "WRONGCODE", ["decision"] = "approve" }));
        Assert.NotEqual(HttpStatusCode.Redirect, r.StatusCode);
    }

    [Fact]
    public async Task Phone_code_flow_requires_matching_pkce_verifier()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var browser = Client();
        await browser.PostAsync("/test/login?email=maya@example.com", null);
        var authorize = await browser.GetAsync("/connect/authorize?client_id=csweet-phone&response_type=code&scope=openid%20email" +
            "&redirect_uri=com.csweet.app:/oauth/callback&state=s1&nonce=n1&code_challenge_method=S256&code_challenge=" + challenge);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(authorize.Headers.Location!.Query);
        var code = query["code"].ToString();

        var app = Client(false);
        var exchange = new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = "csweet-phone", ["redirect_uri"] = "com.csweet.app:/oauth/callback" };
        Assert.Equal("invalid_grant", (await Post(app, "/connect/token", new(exchange) { ["code_verifier"] = verifier + "x" })).GetProperty("error").GetString());

        // A failed attempt with a wrong verifier revokes the code; start again for the success path.
        authorize = await browser.GetAsync("/connect/authorize?client_id=csweet-phone&response_type=code&scope=openid%20email" +
            "&redirect_uri=com.csweet.app:/oauth/callback&state=s2&nonce=n2&code_challenge_method=S256&code_challenge=" + challenge);
        exchange["code"] = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();
        var tokens = await Post(app, "/connect/token", new(exchange) { ["code_verifier"] = verifier });
        var payload = JsonDocument.Parse(Base64Url(tokens.GetProperty("id_token").GetString()!.Split('.')[1])).RootElement;
        Assert.Equal("n2", payload.GetProperty("nonce").GetString());
    }

    [Fact]
    public async Task Wrong_redirect_uri_is_refused()
    {
        var browser = Client();
        await browser.PostAsync("/test/login?email=maya@example.com", null);
        var r = await browser.GetAsync("/connect/authorize?client_id=csweet-phone&response_type=code&scope=openid" +
            "&redirect_uri=https://evil.example/cb&state=s&code_challenge_method=S256&code_challenge=abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqr");
        Assert.True(r.Headers.Location is null || !r.Headers.Location.ToString().StartsWith("https://evil.example"));
    }

    private static string Base64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Encoding.UTF8.GetString(Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')));
    }
}
