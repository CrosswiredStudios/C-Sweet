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
            builder.UseSetting("Installation:DeploymentId", order.ToString());
            builder.UseSetting("Installation:Hostname", host);
            builder.UseSetting("Installation:StateDirectory", folder.FullName);
            builder.ConfigureServices(services =>
            {
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
            async Task<HttpResponseMessage> Login(string subject)
            {
                var challenge = await browser.GetAsync("/api/auth/account/start");
                Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
                var args = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query);
                Assert.Equal(browser.BaseAddress + "api/auth/account/callback", args["redirect_uri"].ToString());
                Assert.Equal("S256", args["code_challenge_method"].ToString());
                issuer.Nonce = args["nonce"].ToString();
                issuer.Challenge = args["code_challenge"].ToString();
                issuer.Subject = subject;
                var callback = await browser.GetAsync(QueryHelpers.AddQueryString("/api/auth/account/callback",
                    new Dictionary<string, string?> { ["code"] = "test-code", ["state"] = args["state"] }));
                Assert.Equal("/api/auth/account/finish", callback.Headers.Location?.OriginalString);
                return await browser.GetAsync(callback.Headers.Location);
            }
            Assert.Equal(HttpStatusCode.Forbidden, (await Login("another-user")).StatusCode);
            var form = await (await Login("buyer")).Content.ReadAsStringAsync();
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
            Assert.Equal("/", (await Login("buyer")).Headers.Location?.OriginalString);
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
            if (request.RequestUri.AbsolutePath.EndsWith("/offices")) return Json(new { linked = 0, standard = 0, team = 0, canManage = true });
            throw new InvalidOperationException(request.RequestUri.AbsolutePath);
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
