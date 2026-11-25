using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CSweet.Contracts.Auth;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CSweet.IntegrationTests;

public sealed partial class AuthenticationEndpointTests
{
    [Fact]
    public async Task Private_office_link_returns_to_local_login_and_delivers_only_a_sealed_grant()
    {
        await using var baseline = CreateFactory(out _);
        await using var app = baseline.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("CSweet:ExecutionGateway:PublicUrl", "https://gateway.private.test:7443");
            builder.ConfigureServices(services => services.ConfigureDbContext<CSweetDbContext>(options =>
                options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))));
        });
        using var device = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://private.test/"), AllowAutoRedirect = false });
        using var key = RSA.Create(2048);
        var request = new CSweet.Office.Contracts.ControlPlane.OfficeLinkRequest("Office PC",
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds());
        var path = "/api/setup/office-links/confirm?" + request.Query;
        var login = await device.GetAsync(path);
        Assert.StartsWith("/login?returnUrl=", login.Headers.Location!.OriginalString);
        Assert.Equal(path, QueryHelpers.ParseQuery(login.Headers.Location.OriginalString.Split('?')[1])["returnUrl"].ToString());
        var poll = "/api/setup/office-links/" + request.Id;
        Assert.Equal(HttpStatusCode.NotFound, (await device.GetAsync(poll)).StatusCode);
        using var browser = CreateHttpsClient(app);
        Assert.Equal(HttpStatusCode.Created, (await browser.PostAsJsonAsync("/api/auth/register", ValidRegistration())).StatusCode);
        var html = await browser.GetStringAsync(path);
        Assert.Contains(request.MatchingCode, html);
        var csrf = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        var fields = new Dictionary<string, string> { ["machine"] = request.Machine, ["key"] = request.Key, ["until"] = request.Until.ToString() };
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync(path, new FormUrlEncodedContent(fields))).StatusCode);
        fields["__RequestVerificationToken"] = csrf;
        Assert.Contains("Return to your Office", await (await browser.PostAsync(path, new FormUrlEncodedContent(fields))).Content.ReadAsStringAsync());
        var grant = await device.GetFromJsonAsync<CSweet.Office.Contracts.ControlPlane.OfficeLinkGrant>(poll);
        Assert.Equal("https://gateway.private.test:7443/", grant!.Gateway);
        Assert.InRange(key.Decrypt(Convert.FromBase64String(grant.SealedToken), RSAEncryptionPadding.OaepSHA256).Length, 32, 256);
    }

    [Fact]
    public async Task Hosted_owner_names_a_workspace_without_a_password_and_returns_to_the_same_dashboard_account()
    {
        var order = Guid.NewGuid();
        var host = $"hq-{order:N}.c-sweet.app";
        var folder = Directory.CreateTempSubdirectory("hosted-browser-");
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        const string authority = "https://account.test/";
        var issuer = new WorkspaceIssuer(order, key);
        await using var baseline = CreateFactory(out _, linkingEnabled: true);
        await using var app = baseline.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Installation:ReservedOwnerSubject", "buyer");
            builder.UseSetting("Installation:Issuer", authority);
            builder.UseSetting("CSweet:ExecutionGateway:PublicUrl", "https://office.fixture.test/");
            builder.UseSetting("Installation:DeploymentId", order.ToString());
            builder.UseSetting("Installation:Hostname", host);
            builder.UseSetting("Installation:StateDirectory", folder.FullName);
            builder.ConfigureServices(services =>
            {
                services.ConfigureDbContext<CSweetDbContext>(options => options.ConfigureWarnings(warnings =>
                    warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
                services.AddHttpClient("CSweetHostedWeb").ConfigurePrimaryHttpMessageHandler(() => issuer);
                services.Configure<OpenIdConnectOptions>("CSweetHostedWeb", o =>
                {
                    o.Configuration = new OpenIdConnectConfiguration { Issuer = authority, AuthorizationEndpoint = authority + "authorize",
                        TokenEndpoint = authority + "token" };
                    o.Configuration.SigningKeys.Add(key);
                    o.BackchannelHttpHandler = issuer;
                });
            });
        });
        try
        {
            using var browser = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://" + host), AllowAutoRedirect = false });
            async Task<HttpResponseMessage> Login(string subject, bool switchAccount = false, string? start = null)
            {
                var challenge = await browser.GetAsync(start ?? (switchAccount ? "/api/auth/account/start?switchAccount=true" : "/api/auth/account/start"));
                Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
                var args = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query);
                Assert.Equal(browser.BaseAddress + "api/auth/account/callback", args["redirect_uri"].ToString());
                Assert.Equal("S256", args["code_challenge_method"].ToString());
                Assert.Equal(switchAccount ? "login" : "", args.TryGetValue("prompt", out var prompt) ? prompt.ToString() : "");
                issuer.Nonce = args["nonce"].ToString();
                issuer.Challenge = args["code_challenge"].ToString();
                issuer.Subject = subject;
                var callback = await browser.GetAsync(QueryHelpers.AddQueryString("/api/auth/account/callback",
                    new Dictionary<string, string?> { ["code"] = "test-code", ["state"] = args["state"] }));
                Assert.Equal("/api/auth/account/finish", callback.Headers.Location?.OriginalString);
                return await browser.GetAsync(callback.Headers.Location);
            }
            Assert.Equal(HttpStatusCode.Forbidden, (await Login("another-user")).StatusCode);
            var form = await (await Login("buyer", switchAccount: true)).Content.ReadAsStringAsync();
            Assert.Contains("Name your Headquarters", form);
            var token = Regex.Match(form, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
            Assert.NotEmpty(token);
            using var finish = await browser.PostAsync("/api/auth/account/name", new FormUrlEncodedContent(new Dictionary<string, string>
                { ["name"] = "Test Headquarters", ["__RequestVerificationToken"] = token }));
            Assert.Equal("/", finish.Headers.Location?.OriginalString);
            var session = await browser.GetFromJsonAsync<AuthStatusResponse>("/api/auth/session");
            Assert.True(session!.IsAuthenticated);
            Assert.True(session.HostedWebSignIn);
            Assert.False(session.HostedOfficesReviewed);
            var overview = await browser.GetAsync("/api/setup/hosted-offices");
            Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
            browser.DefaultRequestHeaders.Add("X-CSWEET-CSRF", session.AntiforgeryToken);
            Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/api/setup/hosted-offices/continue", null)).StatusCode);
            Assert.True((await browser.GetFromJsonAsync<AuthStatusResponse>("/api/auth/session"))!.HostedOfficesReviewed);
            Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/api/auth/logout", null)).StatusCode);
            Assert.False((await browser.GetFromJsonAsync<AuthStatusResponse>("/api/auth/session"))!.IsAuthenticated);
            using var desktop = RSA.Create(2048);
            var request = new CSweet.Office.Contracts.ControlPlane.OfficeLinkRequest("Second Office",
                Convert.ToBase64String(desktop.ExportSubjectPublicKeyInfo()), DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds());
            var confirmation = "/api/setup/office-links/confirm?" + request.Query;
            var redirect = await browser.GetAsync(confirmation);
            Assert.StartsWith("/api/auth/account/start?officeReturn=", redirect.Headers.Location!.OriginalString);
            var resumed = await Login("buyer", start: redirect.Headers.Location.OriginalString);
            Assert.Equal(confirmation, resumed.Headers.Location?.OriginalString);
            browser.DefaultRequestHeaders.Remove("X-CSWEET-CSRF");
            var approvalForm = await browser.GetStringAsync(confirmation);
            Assert.Contains(request.MatchingCode, approvalForm);
            var csrf = Regex.Match(approvalForm, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
            Assert.NotEmpty(csrf);
            async Task<HttpResponseMessage> Confirm(CSweet.Office.Contracts.ControlPlane.OfficeLinkRequest link) =>
                await browser.PostAsync("/api/setup/office-links/confirm", new FormUrlEncodedContent(new Dictionary<string, string>
                    { ["__RequestVerificationToken"] = csrf, ["machine"] = link.Machine, ["key"] = link.Key, ["until"] = link.Until.ToString() }));
            using var approval = await Confirm(request);
            var html = await approval.Content.ReadAsStringAsync();
            Assert.True(approval.IsSuccessStatusCode, html);
            Assert.Contains("Return to your Office", html);
            Assert.Equal(HttpStatusCode.OK, (await Confirm(request)).StatusCode);
            using var device = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = browser.BaseAddress! });
            var grant = await device.GetFromJsonAsync<CSweet.Office.Contracts.ControlPlane.OfficeLinkGrant>("/api/setup/office-links/" + request.Id);
            Assert.Equal("https://office.fixture.test/", grant!.Gateway);
            Assert.InRange(desktop.Decrypt(Convert.FromBase64String(grant.SealedToken), RSAEncryptionPadding.OaepSHA256).Length, 32, 256);
            Assert.Equal(HttpStatusCode.Conflict, (await Confirm(request with { Machine = "Third Office" })).StatusCode);
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            var admin = Assert.Single(await db.Users.ToArrayAsync());
            Assert.Null(admin.PasswordHash);
            Assert.Equal(admin.Id, Assert.Single(await db.UserLogins.ToArrayAsync()).UserId);
        }
        finally { folder.Delete(true); }
    }

    private sealed class WorkspaceIssuer(Guid order, SecurityKey key) : HttpMessageHandler
    {
        public string Nonce = "", Challenge = "", Subject = "";
        private readonly Guid _installation = Guid.NewGuid();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/token")
            {
                var args = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal(Challenge, WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(args["code_verifier"].ToString()))));
                var jwt = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
                {
                    Issuer = "https://account.test/", Audience = $"csweet-hosted-{order:N}",
                    Claims = new Dictionary<string, object> { ["sub"] = Subject, ["email"] = Subject + "@example.test",
                        ["email_verified"] = true, ["nonce"] = Nonce },
                    SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
                });
                return Json(new { id_token = jwt, access_token = "fixture-token", token_type = "Bearer" });
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/client")) return new(HttpStatusCode.NoContent);
            Assert.Equal("Bearer fixture-token", request.Headers.Authorization?.ToString());
            if (request.RequestUri.AbsolutePath.EndsWith("/link")) return Json(new { installationId = _installation, displayName = "Test Headquarters" });
            if (request.RequestUri.AbsolutePath.EndsWith("/offices")) return Json(new { linked = 1, standard = 0, team = 0, canManage = true });
            throw new InvalidOperationException(request.RequestUri.AbsolutePath);
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
