using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using CSweet.Application.Auth;
using CSweet.Contracts.Auth;
using CSweet.Domain.Setup;
using CSweet.HostedAccount;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using AppUser = CSweet.Infrastructure.Auth.ApplicationUser;

namespace CSweet.Api.Auth;

internal sealed record HostedWebTenant(Guid Order, string Owner, Uri Issuer, Uri Origin)
{
    public string ClientId => $"csweet-hosted-{Order:N}";
    public Uri Market(string action) => new(Issuer, $"api/installations/hosted/{Order:D}/{action}");

    public static HostedWebTenant? From(IConfiguration config)
    {
        var options = config.GetSection("Installation").Get<HostedAccountOptions>();
        if (options?.LinkingEnabled != true || string.IsNullOrEmpty(options.ReservedOwnerSubject) ||
            !Guid.TryParse(config["Installation:DeploymentId"], out var order)) return null;
        var host = $"hq-{order:N}.c-sweet.app";
        if (!string.Equals(config["Installation:Hostname"], host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Hosted workspace hostname does not match its deployment ID.");
        return new(order, options.ReservedOwnerSubject, options.IssuerUri, new Uri($"https://{host}/"));
    }
}

internal static class HostedWebSignIn
{
    private const string Scheme = "CSweetHostedWeb";
    private const string Session = "csweet_workspace_session";
    private static readonly string External = IdentityConstants.ExternalScheme;
    internal sealed record Grant(string AccessToken);
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(15);

    public static void AddHostedWebSignIn(this WebApplicationBuilder builder)
    {
        var tenant = HostedWebTenant.From(builder.Configuration);
        if (tenant is null) return;
        builder.Services.AddSingleton(tenant);
        builder.Services.AddMemoryCache();
        builder.Services.AddHttpClient(Scheme, client => client.Timeout = TimeSpan.FromSeconds(12))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        builder.Services.AddAuthentication().AddOpenIdConnect(Scheme, oidc =>
        {
            oidc.Authority = tenant.Issuer.AbsoluteUri;
            oidc.ClientId = tenant.ClientId;
            oidc.SignInScheme = External;
            oidc.ResponseType = OpenIdConnectResponseType.Code;
            oidc.ResponseMode = OpenIdConnectResponseMode.Query;
            oidc.CallbackPath = "/api/auth/account/callback";
            oidc.MapInboundClaims = false;
            oidc.UsePkce = true;
            oidc.Scope.Add("email");
            oidc.Events.OnRedirectToIdentityProvider = context =>
            {
                context.ProtocolMessage.RedirectUri = new Uri(tenant.Origin, oidc.CallbackPath.Value!).AbsoluteUri;
                if (context.Request.Query["switchAccount"] == "true") context.ProtocolMessage.Prompt = "login";
                return Task.CompletedTask;
            };
            oidc.Events.OnTokenValidated = context =>
            {
                var token = context.TokenEndpointResponse?.AccessToken;
                var subject = context.Principal?.FindFirstValue("sub");
                if (string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(token) ||
                    !string.Equals(context.Principal?.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase))
                {
                    context.Fail("A verified C-Sweet account is required.");
                    return Task.CompletedTask;
                }
                var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                context.HttpContext.RequestServices.GetRequiredService<IMemoryCache>().Set(id, new Grant(token), SessionLifetime);
                context.Properties!.Items[Session] = id;
                return Task.CompletedTask;
            };
            oidc.Events.OnRemoteFailure = context =>
            {
                context.HandleResponse();
                context.Response.Redirect("/login?reason=account");
                return Task.CompletedTask;
            };
        });
    }

    internal static async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
    {
        var session = context.Principal?.FindFirstValue(Session);
        if (session is null) return;
        var services = context.HttpContext.RequestServices;
        var cache = services.GetRequiredService<IMemoryCache>();
        if (!cache.TryGetValue<Grant>(session, out var grant) || grant is null) { context.RejectPrincipal(); return; }
        if (cache.TryGetValue("checked-" + session, out _)) return;
        var tenant = services.GetRequiredService<HostedWebTenant>();
        var subject = context.Principal!.FindFirstValue("csweet_account_subject");
        var users = services.GetRequiredService<UserManager<AppUser>>();
        var local = await users.GetUserAsync(context.Principal);
        var db = services.GetRequiredService<CSweetDbContext>();
        var ct = context.HttpContext.RequestAborted;
        if (local is null || !local.EmailConfirmed || await users.IsLockedOutAsync(local) ||
            (await users.FindByLoginAsync(tenant.Issuer.AbsoluteUri, subject!))?.Id != local.Id ||
            !await db.InstallationLinks.AnyAsync(x => x.State == InstallationLinkState.Active && x.Issuer == tenant.Issuer.AbsoluteUri, ct) ||
            (subject != tenant.Owner && !await db.DirectoryPublications.AnyAsync(x => x.Subject == subject &&
                x.ApplicationUserId == local.Id && x.State == DirectoryPublicationState.Active, ct)))
        {
            context.RejectPrincipal();
            return;
        }
        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(Scheme);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", grant.AccessToken);
        try
        {
            using var result = await client.GetAsync(tenant.Market("offices"), ct);
            if (result.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
            {
                cache.Remove(session);
                context.RejectPrincipal();
                return;
            }
        }
        catch (HttpRequestException) { /* The existing session still has a fixed expiration. */ }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
        cache.Set("checked-" + session, true, TimeSpan.FromMinutes(1));
    }

    public static void MapHostedWebSignIn(this WebApplication app)
    {
        if (app.Services.GetService<HostedWebTenant>() is null) return;
        var group = app.MapGroup("/api/auth/account").AllowAnonymous();
        group.MapGet("/start", async (HttpContext http, HostedWebTenant tenant, IHttpClientFactory clients, CancellationToken ct) =>
        {
            using var client = clients.CreateClient(Scheme);
            using var prepared = await client.PostAsync(tenant.Market("client"), null, ct);
            if (!prepared.IsSuccessStatusCode) return Results.Problem("C-Sweet could not prepare sign-in for this Headquarters.", statusCode:503);
            var properties = new AuthenticationProperties { RedirectUri = "/api/auth/account/finish" };
            var resume = http.Request.Query["officeReturn"].ToString();
            if (resume.Length <= 1500 && resume.StartsWith("/api/setup/office-links/confirm?", StringComparison.Ordinal) &&
                !resume.Any(char.IsControl)) properties.Items["officeReturn"] = resume;
            return Results.Challenge(properties, [Scheme]);
        });
        group.MapGet("/finish", FinishAsync);
        group.MapPost("/name", FinishAsync).DisableAntiforgery();
    }

    private static async Task<IResult> FinishAsync(HttpContext http, HostedWebTenant tenant, IHttpClientFactory clients,
        IMemoryCache cache, CSweetDbContext db, UserManager<AppUser> users, SignInManager<AppUser> signIn,
        IAntiforgery csrf, DirectoryPublicationSignal directory, IDataProtectionProvider protection,
        CSweet.Application.Auth.IAuthenticationService auth, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var ticket = await http.AuthenticateAsync(External);
        var subject = ticket.Principal?.FindFirstValue("sub");
        var email = ticket.Principal?.FindFirstValue("email");
        if (!ticket.Succeeded || subject is null || string.IsNullOrWhiteSpace(email) ||
            ticket.Properties is null || !ticket.Properties.Items.TryGetValue(Session, out var session) || session is null ||
            !cache.TryGetValue<Grant>(session, out var grant) || grant is null)
            return Results.Redirect("/api/auth/account/start");
        var owner = subject == tenant.Owner;
        var account = await users.FindByLoginAsync(tenant.Issuer.AbsoluteUri, subject);
        using var client = clients.CreateClient(Scheme);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", grant.AccessToken);
        if (account is null)
        {
            if (!owner || await db.Users.AnyAsync(x => x.IsInitialAdministrator, ct)) return Results.StatusCode(403);
            if (!HttpMethods.IsPost(http.Request.Method))
            {
                var token = csrf.GetAndStoreTokens(http);
                return Results.Content($"""
                    <!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
                    <title>Name your Headquarters</title><main style="max-width:34rem;margin:12vh auto;padding:1rem;font:1.1rem system-ui">
                    <h1>Name your Headquarters</h1><p>Signed in as {HtmlEncoder.Default.Encode(email)}</p>
                    <form method="post" action="/api/auth/account/name"><input type="hidden" name="{token.FormFieldName}" value="{HtmlEncoder.Default.Encode(token.RequestToken!)}">
                    <label>Headquarters name <input name="name" required maxlength="80"></label> <button>Continue</button></form></main></html>
                    """, "text/html");
            }
            if (!await csrf.IsRequestValidAsync(http)) return Results.BadRequest();
            var name = (await http.Request.ReadFormAsync(ct))["name"].ToString().Trim();
            if (name.Length is 0 or > 80 || name.Any(char.IsControl)) return Results.BadRequest();
            using var key = EnrollmentKey(tenant, protection, http.RequestServices.GetRequiredService<IConfiguration>());
            var registration = await BindAsync(client, tenant, key, name, ct);
            if (registration is null) return Results.Problem("C-Sweet could not link this Headquarters. Retry setup.", statusCode:503);
            var result = await auth.RegisterHostedAsync(new RegisterAdminRequest(email, email, "", ""),
                new HostedAdministratorLink(tenant.Issuer.AbsoluteUri, subject, email, registration.InstallationId,
                    registration.DisplayName, key.ExportPrivateKey()) { Passwordless = true }, ct);
            if (!result.Succeeded) return Results.Conflict();
            directory.Notify();
            account = await users.FindByLoginAsync(tenant.Issuer.AbsoluteUri, subject);
        }
        if (account is null || !await signIn.CanSignInAsync(account) || await users.IsLockedOutAsync(account)) return Results.StatusCode(403);
        var link = await db.InstallationLinks.SingleOrDefaultAsync(x => x.State == InstallationLinkState.Active &&
            x.Issuer == tenant.Issuer.AbsoluteUri, ct);
        if (link is null) return Results.StatusCode(403);
        if (owner)
        {
            if (!account.IsInitialAdministrator || link.LinkedByApplicationUserId != account.Id) return Results.StatusCode(403);
            var protector = protection.CreateProtector(CSweet.Infrastructure.Auth.AuthenticationService.InstallationKeyProtectionPurpose);
            using var key = InstallationKey.Import(protector.Unprotect(Convert.FromBase64String(link.ProtectedInstallationKey)));
            var bound = await BindAsync(client, tenant, key, link.DisplayName, ct);
            if (bound?.InstallationId != link.InstallationId) return Results.StatusCode(403);
        }
        else if (!await db.DirectoryPublications.AnyAsync(x => x.Subject == subject && x.ApplicationUserId == account.Id &&
            x.State == DirectoryPublicationState.Active, ct)) return Results.StatusCode(403);
        await http.SignOutAsync(External);
        await signIn.SignInWithClaimsAsync(account, new AuthenticationProperties { AllowRefresh = false, ExpiresUtc = DateTimeOffset.UtcNow.Add(SessionLifetime) },
            [new Claim(Session, session), new Claim("csweet_account_subject", subject)]);
        return Results.LocalRedirect(ticket.Properties.Items.TryGetValue("officeReturn", out var resume) && resume != null ? resume : "/");
    }

    private static async Task<InstallationRegistration?> BindAsync(HttpClient client, HostedWebTenant tenant, InstallationKey key, string name, CancellationToken ct)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var response = await client.PostAsJsonAsync(tenant.Market("link"), new { key.PublicKey, DisplayName = name, Timestamp = timestamp,
            Signature = key.SignHostedWorkspace(tenant.Order, name, timestamp) }, ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<InstallationRegistration>(ct) : null;
    }

    private static InstallationKey EnrollmentKey(HostedWebTenant tenant, IDataProtectionProvider protection, IConfiguration config)
    {
        // Persist before contacting Market so a crash between remote and local commits reuses the same key.
        var protector = protection.CreateProtector($"CSweet.HostedEnrollment.{tenant.Order:N}");
        var path = Path.Combine(config["Installation:StateDirectory"] ?? "/state", $"hosted-{tenant.Order:N}.key");
        if (!File.Exists(path))
        {
            using var key = InstallationKey.Create();
            var temp = path + "." + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temp, protector.Protect(key.ExportPrivateKey()));
            try { File.Move(temp, path); }
            catch (IOException) when (File.Exists(path)) { File.Delete(temp); }
        }
        return InstallationKey.Import(protector.Unprotect(File.ReadAllBytes(path)));
    }

    internal static async Task<HttpResponseMessage?> GetOfficesAsync(HttpContext http, HostedWebTenant tenant, IMemoryCache cache,
        IHttpClientFactory clients, CancellationToken ct)
    {
        var session = http.User.FindFirstValue(Session);
        if (session is null || !cache.TryGetValue<Grant>(session, out var grant) || grant is null) return null;
        using var client = clients.CreateClient(Scheme);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", grant.AccessToken);
        return await client.GetAsync(tenant.Market("offices"), ct);
    }
}
