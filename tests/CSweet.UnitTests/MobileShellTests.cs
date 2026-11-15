using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.UI.Localization;
using CSweet.UI.Mobile;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace CSweet.UnitTests;

/// <summary>Phone shell: sign in with C-Sweet (IL-07), launch rules, My Headquarters and sign-out (H3).</summary>
public sealed class MobileShellTests
{
    private const string Issuer = "https://account.test/";
    private readonly FakeTime _time = new(DateTimeOffset.Parse("2026-11-14T12:00:00Z"));
    private readonly FakeIssuer _issuer;
    private readonly FakeBrowser _browser;
    private readonly InMemorySecureValueStore _storage = new();
    private readonly FakeConnector _connector = new();
    private readonly FakeEnterpriseApp _app = new();

    public MobileShellTests()
    {
        _issuer = new FakeIssuer(_time);
        _browser = new FakeBrowser(_issuer);
    }

    [Fact]
    public async Task Fresh_install_opens_sign_in_without_contacting_CSweet()
    {
        var shell = Shell();
        await shell.StartAsync();
        Assert.Equal(MobileShellState.Entry, shell.State);
        Assert.Empty(_issuer.Requests);
    }

    [Fact]
    public async Task Sign_in_uses_pkce_state_and_nonce_then_opens_the_only_headquarters()
    {
        _issuer.Directory.Add(new("Crosswired Studios", "Owner"));
        var shell = Shell();
        await shell.StartAsync();
        await shell.SignInAsync(createAccount: false);

        var start = _browser.Starts.Single();
        Assert.StartsWith("https://account.test/connect/authorize?", start.AbsoluteUri);
        var query = ParseQuery(start.Query);
        Assert.Equal("csweet-app", query["client_id"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Contains("directory", query["scope"].Split(' '));
        Assert.Contains("offline_access", query["scope"].Split(' '));
        Assert.Equal("com.csweet.app:/oauth/callback", query["redirect_uri"]);
        Assert.False(string.IsNullOrEmpty(query["state"]));
        var verifier = _issuer.LastTokenForm!["code_verifier"];
        Assert.Equal(Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), query["code_challenge"]);

        Assert.Equal(MobileShellState.Connecting, shell.State);
        Assert.Equal("Crosswired Studios", shell.ActiveProfile!.DisplayName);
        Assert.Equal("maya@example.com", shell.AccountEmail);
        Assert.True(_storage.Values.ContainsKey("csweet.account.v1"));
        Assert.Equal(shell.ActiveProfile.ProfileId, _storage.Values["csweet.profiles.last.v1"]);
    }

    [Fact]
    public async Task Create_account_starts_at_Market_registration_and_returns_to_the_same_request()
    {
        var shell = Shell();
        await shell.StartAsync();
        await shell.SignInAsync(createAccount: true);
        var start = _browser.Starts.Single();
        Assert.StartsWith("https://account.test/Account/Register?ReturnUrl=", start.AbsoluteUri);
        var returnUrl = ParseQuery(start.Query)["ReturnUrl"];
        Assert.StartsWith("/connect/authorize?client_id=csweet-app", returnUrl);
        Assert.Equal(MobileShellState.Chooser, shell.State); // signed in, no Headquarters yet
    }

    [Fact]
    public async Task Several_headquarters_open_the_chooser_and_same_names_get_a_second_line()
    {
        _issuer.Directory.Add(new("Northstar", "Owner"));
        _issuer.Directory.Add(new("Harbor", "Member"));
        _issuer.Directory.Add(new("northstar", "Owner"));
        var shell = await SignedIn();
        Assert.Equal(MobileShellState.Chooser, shell.State);
        Assert.Null(shell.ActiveProfile);
        Assert.Equal("Harbor", shell.Profiles[0].DisplayName);
        Assert.Equal(3, shell.Profiles.Count);
        Assert.Null(shell.Profiles.Single(x => x.DisplayName == "Harbor").SecondLine);
        Assert.All(shell.Profiles.Where(x => x.DisplayName != "Harbor"), x => Assert.Equal(x.HeadquartersId.ToString("N")[..8], x.SecondLine));

        var html = await Render(shell);
        Assert.Contains("My Headquarters", html);
        Assert.Contains("maya@example.com", html);
        Assert.Contains("ID " + shell.Profiles.First(x => x.SecondLine is not null).SecondLine, html);
        Assert.DoesNotContain("aria-current", html);

        await shell.SelectAsync(shell.Profiles.Single(x => x.DisplayName == "Harbor").ProfileId);
        Assert.Equal(MobileShellState.Connecting, shell.State);
        html = WebUtility.HtmlDecode(await Render(shell));
        Assert.Contains("Connection pending", html);
        Assert.Contains("This phone isn't connected to Harbor yet.", html);
        Assert.Contains("Switch enterprise", html);

        shell.OpenChooser();
        html = await Render(shell);
        Assert.Contains("aria-current=\"true\"", html);
        Assert.Contains("aria-label=\"Current enterprise: Harbor\"", html);
    }

    [Fact]
    public async Task No_headquarters_shows_the_empty_chooser()
    {
        var shell = await SignedIn();
        Assert.Equal(MobileShellState.Chooser, shell.State);
        var html = WebUtility.HtmlDecode(await Render(shell));
        Assert.Contains("You haven't joined an enterprise yet", html);
    }

    [Fact]
    public async Task Cancelled_browser_leaves_sign_in_usable()
    {
        var shell = Shell();
        await shell.StartAsync();
        _browser.Cancel = true;
        await shell.SignInAsync(false);
        Assert.Equal(MobileShellState.Entry, shell.State);
        Assert.Equal(MobileShellNotice.SignInCancelled, shell.Notice);
        Assert.Contains("Sign-in was cancelled", await Render(shell));
        Assert.DoesNotContain(_storage.Values.Keys, x => x != "csweet.device-key.v1");

        _browser.Cancel = false;
        await shell.SignInAsync(false);
        Assert.Equal(MobileShellState.Chooser, shell.State);
        Assert.Equal(MobileShellNotice.None, shell.Notice);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("nonce")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("error")]
    public async Task Tampered_or_refused_sign_in_is_rejected_and_nothing_is_saved(string defect)
    {
        _browser.WrongState = defect == "state";
        _browser.Error = defect == "error" ? "access_denied" : null;
        _issuer.IdTokenDefect = defect;
        var shell = Shell();
        await shell.StartAsync();
        await shell.SignInAsync(false);
        Assert.Equal(MobileShellState.Entry, shell.State);
        Assert.Equal(MobileShellNotice.SignInFailed, shell.Notice);
        Assert.DoesNotContain(_storage.Values.Keys, x => x != "csweet.device-key.v1");
        Assert.Contains("Couldn't sign in", WebUtility.HtmlDecode(await Render(shell)));
    }

    [Fact]
    public async Task Unreachable_CSweet_reports_a_failed_sign_in()
    {
        _issuer.Offline = true;
        var shell = Shell();
        await shell.StartAsync();
        await shell.SignInAsync(false);
        Assert.Equal(MobileShellNotice.SignInFailed, shell.Notice);
        Assert.Empty(_browser.Starts);
    }

    [Fact]
    public async Task Relaunch_restores_the_last_selection_and_follows_directory_changes()
    {
        _issuer.Directory.Add(new("Northstar", "Owner"));
        _issuer.Directory.Add(new("Harbor", "Owner"));
        var first = await SignedIn();
        await first.SelectAsync(first.Profiles.Single(x => x.DisplayName == "Harbor").ProfileId);

        // Renamed while the app was closed.
        _issuer.Directory[1] = _issuer.Directory[1] with { Name = "Harbor Labs" };
        var relaunched = Shell();
        await relaunched.StartAsync();
        Assert.Equal(MobileShellState.Connecting, relaunched.State);
        Assert.Equal("Harbor Labs", relaunched.ActiveProfile!.DisplayName);

        // Access removed: the selection is forgotten and the chooser opens.
        _issuer.Directory.RemoveAt(1);
        var again = Shell();
        await again.StartAsync();
        Assert.Equal(MobileShellState.Chooser, again.State);
        Assert.Null(again.ActiveProfile);
        Assert.False(_storage.Values.ContainsKey("csweet.profiles.last.v1"));
    }

    [Fact]
    public async Task Directory_outage_keeps_the_saved_list_and_offers_retry()
    {
        _issuer.Directory.Add(new("Northstar", "Owner"));
        _issuer.Directory.Add(new("Harbor", "Owner"));
        await SignedIn();
        _issuer.DirectoryStatus = HttpStatusCode.ServiceUnavailable;
        var relaunched = Shell();
        await relaunched.StartAsync();
        Assert.Equal(MobileShellState.Chooser, relaunched.State);
        Assert.Equal(MobileShellNotice.DirectoryUnavailable, relaunched.Notice);
        Assert.Equal(2, relaunched.Profiles.Count);
        Assert.Contains("Couldn't refresh your Headquarters list.", WebUtility.HtmlDecode(await Render(relaunched)));

        _issuer.DirectoryStatus = HttpStatusCode.OK;
        await relaunched.RefreshDirectoryAsync();
        Assert.Equal(MobileShellNotice.None, relaunched.Notice);
    }

    [Fact]
    public async Task Expired_access_is_refreshed_and_a_refused_refresh_signs_out_and_clears_the_list()
    {
        _issuer.Directory.Add(new("Northstar", "Owner"));
        _issuer.Directory.Add(new("Harbor", "Owner"));
        var shell = await SignedIn();
        _time.Advance(TimeSpan.FromHours(2));
        await shell.RefreshDirectoryAsync();
        Assert.Equal("refresh_token", _issuer.LastTokenForm!["grant_type"]);
        Assert.Equal(MobileShellNotice.None, shell.Notice);

        _time.Advance(TimeSpan.FromHours(2));
        _issuer.RefreshRefused = true;
        await shell.RefreshDirectoryAsync();
        Assert.Equal(MobileShellState.Entry, shell.State);
        Assert.Equal(MobileShellNotice.SignInExpired, shell.Notice);
        Assert.Empty(shell.Profiles);
        Assert.DoesNotContain(_storage.Values.Keys, x => x != "csweet.device-key.v1");
    }

    [Fact]
    public async Task Sign_out_forgets_the_account_and_its_list_and_another_account_starts_clean()
    {
        _issuer.Directory.Add(new("Northstar", "Owner"));
        var shell = await SignedIn();
        var generation = shell.Generation;
        await shell.SignOutAsync();
        Assert.Equal(MobileShellState.Entry, shell.State);
        Assert.True(shell.Generation > generation);
        Assert.DoesNotContain(_storage.Values.Keys, x => x != "csweet.device-key.v1");

        _issuer.Directory.Clear();
        _issuer.Subject = "market-user-2";
        await shell.SignInAsync(false);
        Assert.Equal(MobileShellState.Chooser, shell.State);
        Assert.Empty(shell.Profiles);
    }

    [Fact]
    public async Task Every_selection_increments_the_generation_even_for_the_current_one()
    {
        _issuer.Directory.Add(new("Northstar", "Owner"));
        _issuer.Directory.Add(new("Harbor", "Owner"));
        var shell = await SignedIn();
        var id = shell.Profiles[0].ProfileId;
        await shell.SelectAsync(id);
        var first = shell.Generation;
        await shell.SelectAsync(id);
        Assert.Equal(first + 1, shell.Generation);
    }

    [Fact]
    public async Task An_active_phone_can_open_the_enterprise_app_when_the_host_has_one()
    {
        _app.IsAvailable = true;
        _connector.Result = new(HeadquartersConnectStatus.Connected, "Tls13", "Crosswired Studios", "active", ["Qwen Games"]);
        _issuer.Directory.Add(new("Crosswired Studios", "Owner"));
        _issuer.RelayConfigured = true;
        var shell = await SignedIn();
        await shell.ConnectAsync();
        var html = WebUtility.HtmlDecode(await Render(shell));
        Assert.Contains("Open Crosswired Studios", html);
        Assert.Contains("Qwen Games", html);
        Assert.Equal(_issuer.Directory.Single().Id, _connector.Installations.Single());

        _app.IsAvailable = false;
        Assert.DoesNotContain("Open Crosswired Studios", WebUtility.HtmlDecode(await Render(shell)));
    }

    [Fact]
    public async Task Connect_gets_a_ticket_for_this_phones_key_and_reports_that_enrollment_is_needed()
    {
        _issuer.Directory.Add(new("Crosswired Studios", "Owner"));
        _issuer.RelayConfigured = true;
        var shell = await SignedIn();
        var html = await Render(shell);
        Assert.Contains(">Connect<", html.Replace(" ", "").Replace("\n", ""));

        await shell.ConnectAsync();
        var ticket = Assert.Single(_connector.Tickets);
        Assert.Equal(("https://relay.test/", "route-1", "HQPIN"), (ticket.RelayOrigin, ticket.RouteId, ticket.HeadquartersPin));
        Assert.Equal(_issuer.LastPhoneKey, MobileDeviceKeyStore.PublicKey(_connector.Keys.Single()));
        Assert.Equal(HeadquartersConnectStatus.Connected, shell.LastConnection!.Status);
        html = WebUtility.HtmlDecode(await Render(shell));
        Assert.Contains("Connected securely to Crosswired Studios (TLS 1.3).", html);
        Assert.Contains("still needs approval at Crosswired Studios", html);
        Assert.Contains("open Settings → Phones", html);
        Assert.Contains("This phone's fingerprint: " + shell.DeviceFingerprint, html);
        Assert.Contains("Check again", html);
        Assert.Equal("assertion-1", ticket.DeviceAssertion);

        // The device key is created once and reused.
        await shell.ConnectAsync();
        Assert.Equal(MobileDeviceKeyStore.PublicKey(_connector.Keys[0]), MobileDeviceKeyStore.PublicKey(_connector.Keys[1]));
    }

    [Fact]
    public async Task An_approved_phone_shows_the_businesses_Headquarters_returned()
    {
        _issuer.Directory.Add(new("Crosswired Studios", "Owner"));
        _issuer.RelayConfigured = true;
        _connector.Result = new(HeadquartersConnectStatus.Connected, "Tls13", "Crosswired Studios", "active", ["Games", "Media"]);
        var shell = await SignedIn();
        await shell.ConnectAsync();
        var html = WebUtility.HtmlDecode(await Render(shell));
        Assert.Contains("Your businesses", html);
        Assert.Contains(">Games<", html);
        Assert.Contains(">Media<", html);
        Assert.DoesNotContain("Check again", html);
    }

    [Theory]
    [InlineData("denied", "This phone was denied at Crosswired Studios.")]
    [InlineData("revoked", "access to Crosswired Studios was revoked")]
    [InlineData("no_access", "doesn't have access to Crosswired Studios")]
    [InlineData("rejected", "didn't accept this phone's sign-in")]
    public async Task Enrollment_outcomes_other_than_pending_or_active_are_explained(string enrollment, string expected)
    {
        _issuer.Directory.Add(new("Crosswired Studios", "Owner"));
        _issuer.RelayConfigured = true;
        _connector.Result = new(HeadquartersConnectStatus.Connected, "Tls13", "Crosswired Studios", enrollment);
        var shell = await SignedIn();
        await shell.ConnectAsync();
        Assert.Contains(expected, WebUtility.HtmlDecode(await Render(shell)));
    }

    [Theory]
    [InlineData("not_configured", "hasn't turned on phone access yet")]
    [InlineData("not_member", "don't have access to Crosswired Studios anymore")]
    [InlineData("unreachable", "Couldn't reach Crosswired Studios")]
    [InlineData("secure", "secure connection to Crosswired Studios failed")]
    public async Task Connect_failures_explain_what_happened_and_offer_retry(string failure, string expected)
    {
        _issuer.Directory.Add(new("Crosswired Studios", "Owner"));
        _issuer.RelayConfigured = failure != "not_configured";
        _issuer.TicketNotFound = failure == "not_member";
        _connector.Result = failure switch
        {
            "unreachable" => new HeadquartersConnectResult(HeadquartersConnectStatus.Unreachable),
            "secure" => new HeadquartersConnectResult(HeadquartersConnectStatus.SecureConnectionFailed),
            _ => _connector.Result
        };
        var shell = await SignedIn();
        await shell.ConnectAsync();
        var html = WebUtility.HtmlDecode(await Render(shell));
        Assert.Contains(expected, html);
        Assert.Contains("Try again", html);
    }

    [Fact]
    public async Task Switching_enterprise_clears_the_last_connection_result()
    {
        _issuer.Directory.Add(new("Northstar", "Owner"));
        _issuer.Directory.Add(new("Harbor", "Owner"));
        _issuer.RelayConfigured = true;
        var shell = await SignedIn();
        await shell.SelectAsync(shell.Profiles[0].ProfileId);
        await shell.ConnectAsync();
        Assert.NotNull(shell.LastConnection);
        await shell.SelectAsync(shell.Profiles[1].ProfileId);
        Assert.Null(shell.LastConnection);
    }

    [Fact]
    public void Release_options_require_https()
    {
        Assert.Throws<InvalidOperationException>(() => new MobileAccountOptions { Issuer = new Uri("http://localhost:5163/") }.Validate());
        new MobileAccountOptions { Issuer = new Uri("http://localhost:5163/"), AllowHttpIssuer = true }.Validate();
        new MobileAccountOptions().Validate();
    }

    private async Task<MobileSessionCoordinator> SignedIn()
    {
        var shell = Shell();
        await shell.StartAsync();
        await shell.SignInAsync(false);
        return shell;
    }

    private MobileSessionCoordinator Shell()
    {
        var options = new MobileAccountOptions { Issuer = new Uri(Issuer) };
        var http = new HttpClient(_issuer);
        var account = new MobileAccountSession(new AccountSignInClient(http, options, _time), _storage, _time);
        return new MobileSessionCoordinator(account, new HeadquartersDirectoryClient(http, options), new SecureEnterpriseProfileStore(_storage), _browser,
            new MobileDeviceKeyStore(_storage), _connector);
    }

    private async Task<string> Render(MobileSessionCoordinator shell)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddCSweetLocalization();
        services.AddSingleton<IJSRuntime, NoJs>();
        services.AddSingleton(shell);
        services.AddSingleton<IEnterpriseAppHost>(_app);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<MobileRoot>(ParameterView.Empty)).ToHtmlString());
    }

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2))
            .ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x.Length > 1 ? x[1] : ""));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record Hq(string Name, string Role)
    {
        public Guid Id { get; init; } = Guid.NewGuid();
    }

    private sealed class FakeIssuer(FakeTime time) : HttpMessageHandler
    {
        public List<Hq> Directory { get; } = [];
        public List<string> Requests { get; } = [];
        public Dictionary<string, string>? LastTokenForm { get; private set; }
        public string? IdTokenDefect { get; set; }
        public string Subject { get; set; } = "market-user-1";
        public bool Offline { get; set; }
        public bool RefreshRefused { get; set; }
        public HttpStatusCode DirectoryStatus { get; set; } = HttpStatusCode.OK;
        public bool RelayConfigured { get; set; }
        public bool TicketNotFound { get; set; }
        public string? LastPhoneKey { get; private set; }
        public string? LastNonce { get; set; }
        private int _tokens;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            if (Offline) throw new HttpRequestException("offline");
            switch (path)
            {
                case "/.well-known/openid-configuration":
                    return Json(new Dictionary<string, string>
                    {
                        ["issuer"] = Issuer,
                        ["authorization_endpoint"] = Issuer + "connect/authorize",
                        ["token_endpoint"] = Issuer + "connect/token"
                    });
                case "/connect/token":
                    LastTokenForm = (await request.Content!.ReadAsStringAsync(ct)).Split('&').Select(x => x.Split('='))
                        .ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1]));
                    if (LastTokenForm["grant_type"] == "refresh_token" && RefreshRefused)
                        return Json(new { error = "invalid_grant" }, HttpStatusCode.BadRequest);
                    _tokens++;
                    return Json(new
                    {
                        access_token = $"access-{_tokens}",
                        refresh_token = $"refresh-{_tokens}",
                        expires_in = 3600,
                        id_token = LastTokenForm["grant_type"] == "authorization_code" ? IdToken() : null
                    });
                case var ticket when ticket.StartsWith("/api/directory/installations/", StringComparison.Ordinal) && ticket.EndsWith("/ticket", StringComparison.Ordinal):
                    if (request.Headers.Authorization?.Parameter != $"access-{_tokens}") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                    LastPhoneKey = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.GetProperty("phoneKey").GetString();
                    if (TicketNotFound) return new HttpResponseMessage(HttpStatusCode.NotFound);
                    if (!RelayConfigured) return Json(new { errorCode = "relay_not_configured" }, HttpStatusCode.Conflict);
                    return Json(new { ticket = "body.sig", relayOrigin = "https://relay.test/", routeId = "route-1", headquartersPin = "HQPIN", expiresAt = time.GetUtcNow().AddMinutes(5), deviceAssertion = "assertion-1" });
                case "/api/directory/installations":
                    if (request.Headers.Authorization?.Parameter != $"access-{_tokens}") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                    if (DirectoryStatus != HttpStatusCode.OK) return new HttpResponseMessage(DirectoryStatus);
                    return Json(new { installations = Directory.Select(x => new { installationId = x.Id, displayName = x.Name, role = x.Role }) });
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private string IdToken()
        {
            var payload = new Dictionary<string, object?>
            {
                ["iss"] = IdTokenDefect == "issuer" ? "https://other.test/" : Issuer,
                ["aud"] = IdTokenDefect == "audience" ? "someone-else" : "csweet-app",
                ["sub"] = Subject,
                ["email"] = "maya@example.com",
                ["nonce"] = IdTokenDefect == "nonce" ? "wrong" : LastNonce,
                ["exp"] = time.GetUtcNow().AddMinutes(10).ToUnixTimeSeconds()
            };
            return "eyJhbGciOiJSUzI1NiJ9." + Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload)) + ".c2ln";
        }

        private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = JsonContent.Create(value) };
    }

    private sealed class FakeBrowser(FakeIssuer issuer) : IBrowserAuthenticator
    {
        public List<Uri> Starts { get; } = [];
        public bool Cancel { get; set; }
        public bool WrongState { get; set; }
        public string? Error { get; set; }

        public Task<BrowserResult> AuthenticateAsync(Uri start, Uri callback, CancellationToken ct)
        {
            Starts.Add(start);
            Assert.Equal("com.csweet.app:/oauth/callback", callback.OriginalString);
            if (Cancel) return Task.FromResult(new BrowserResult(null));
            var query = ParseQuery(start.Query);
            if (query.TryGetValue("ReturnUrl", out var returnUrl))
                query = ParseQuery(new Uri(new Uri(Issuer), returnUrl).Query);
            issuer.LastNonce = query["nonce"];
            var result = new Dictionary<string, string> { ["state"] = WrongState ? "forged" : query["state"] };
            if (Error is not null) result["error"] = Error;
            else result["code"] = "code-1";
            return Task.FromResult(new BrowserResult(result));
        }
    }

    private sealed class FakeEnterpriseApp : IEnterpriseAppHost
    {
        public bool IsAvailable { get; set; }
        public List<(Guid, string)> Opened { get; } = [];
        public Task<bool> OpenAsync(Guid installationId, string displayName) { Opened.Add((installationId, displayName)); return Task.FromResult(true); }
    }

    private sealed class FakeConnector : IHeadquartersConnector
    {
        public List<ConnectionTicket> Tickets { get; } = [];
        public List<ECDsa> Keys { get; } = [];
        public List<Guid> Installations { get; } = [];
        public HeadquartersConnectResult Result { get; set; } = new(HeadquartersConnectStatus.Connected, "Tls13", "Crosswired Studios", "pending");

        public Task<HeadquartersConnectResult> ConnectAsync(Guid installationId, ConnectionTicket ticket, ECDsa deviceKey, CancellationToken ct)
        {
            Installations.Add(installationId);
            Tickets.Add(ticket);
            Keys.Add(deviceKey);
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>The WebView has no stored language in tests, so the shell follows the default culture.</summary>
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new JSException("No JavaScript in tests.");
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new JSException("No JavaScript in tests.");
    }
}
