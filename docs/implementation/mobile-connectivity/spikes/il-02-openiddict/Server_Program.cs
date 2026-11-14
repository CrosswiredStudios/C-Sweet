// IL-02 spike: Market-shaped Identity store + OpenIddict server with device authorization (Headquarters)
// and authorization code + PKCE (phone). Throwaway; findings recorded in the C-Sweet decision log.
using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

var builder = WebApplication.CreateBuilder(args);
var connection = new SqliteConnection("Data Source=:memory:");
connection.Open();
builder.Services.AddDbContext<SpikeDb>(o => { o.UseSqlite(connection); o.UseOpenIddict(); });
builder.Services.AddIdentity<IdentityUser, IdentityRole>(o => o.SignIn.RequireConfirmedAccount = true)
    .AddEntityFrameworkStores<SpikeDb>().AddDefaultTokenProviders();

builder.Services.AddOpenIddict()
    .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<SpikeDb>())
    .AddServer(o =>
    {
        o.SetAuthorizationEndpointUris("connect/authorize")
         .SetTokenEndpointUris("connect/token")
         .SetDeviceAuthorizationEndpointUris("connect/device")
         .SetEndUserVerificationEndpointUris("connect/verify");
        o.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange()
         .AllowDeviceAuthorizationFlow();
        o.RegisterScopes(Scopes.OpenId, Scopes.Email, "installation");
        o.AddEphemeralEncryptionKey().AddEphemeralSigningKey().DisableAccessTokenEncryption();
        o.UseAspNetCore()
         .EnableAuthorizationEndpointPassthrough()
         .EnableEndUserVerificationEndpointPassthrough()
         .EnableTokenEndpointPassthrough();
    });

builder.Services.AddAuthorization();
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SpikeDb>();
    await db.Database.EnsureCreatedAsync();
    var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
    await apps.CreateAsync(new OpenIddictApplicationDescriptor
    {
        ClientId = "csweet-hq", ClientType = ClientTypes.Public, DisplayName = "C-Sweet Headquarters",
        Permissions = { Permissions.Endpoints.DeviceAuthorization, Permissions.Endpoints.Token,
            Permissions.GrantTypes.DeviceCode, Permissions.Scopes.Email, Permissions.Prefixes.Scope + "installation" }
    });
    await apps.CreateAsync(new OpenIddictApplicationDescriptor
    {
        ClientId = "csweet-phone", ClientType = ClientTypes.Public, DisplayName = "C-Sweet app",
        RedirectUris = { new Uri("com.csweet.app:/oauth/callback") },
        Permissions = { Permissions.Endpoints.Authorization, Permissions.Endpoints.Token,
            Permissions.GrantTypes.AuthorizationCode, Permissions.ResponseTypes.Code, Permissions.Scopes.Email },
        Requirements = { Requirements.Features.ProofKeyForCodeExchange }
    });
    var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
    var maya = new IdentityUser { UserName = "maya@example.com", Email = "maya@example.com", EmailConfirmed = true };
    await users.CreateAsync(maya, "Password1!");
}

// Stand-in for the Market login page: ordinary Identity cookie sign-in.
app.MapPost("/test/login", async (string email, SignInManager<IdentityUser> signIn) =>
{
    var result = await signIn.PasswordSignInAsync(email, "Password1!", false, false);
    return result.Succeeded ? Results.Ok() : Results.Unauthorized();
});
app.MapGet("/test/whoami", (ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true ? Results.Ok(user.Identity.Name) : Results.Unauthorized());

// Device approval page (Market /device). The user is signed in with the normal Market cookie.
app.MapPost("/connect/verify", async (HttpContext http, UserManager<IdentityUser> users) =>
{
    var request = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    if (request.Principal is null) return Results.BadRequest("unknown or expired code");
    var user = await users.GetUserAsync(http.User);
    if (user is null) return Results.Challenge(authenticationSchemes: [IdentityConstants.ApplicationScheme]);
    var form = await http.Request.ReadFormAsync();
    if (form["decision"] != "approve")
        return Results.Forbid(authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    var identity = Identity(user, request.Principal.GetScopes());
    return Results.SignIn(new ClaimsPrincipal(identity), new() { RedirectUri = "/device/done" }, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
});

// Authorization endpoint for the phone (code + PKCE through the system browser).
app.MapMethods("/connect/authorize", ["GET", "POST"], async (HttpContext http, UserManager<IdentityUser> users) =>
{
    var request = http.GetOpenIddictServerRequest()!;
    var user = await users.GetUserAsync(http.User);
    if (user is null) return Results.Challenge(authenticationSchemes: [IdentityConstants.ApplicationScheme]);
    return Results.SignIn(new ClaimsPrincipal(Identity(user, request.GetScopes())), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
});

// Token endpoint: code and device grants reuse the principal stored at approval, after re-checking the user.
app.MapPost("/connect/token", async (HttpContext http, UserManager<IdentityUser> users) =>
{
    var request = http.GetOpenIddictServerRequest()!;
    if (!request.IsAuthorizationCodeGrantType() && !request.IsDeviceCodeGrantType())
        return Results.BadRequest();
    var result = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    var user = await users.FindByIdAsync(result.Principal!.GetClaim(Claims.Subject)!);
    if (user is null || !await users.IsEmailConfirmedAsync(user))
        return Results.Forbid(authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    return Results.SignIn(new ClaimsPrincipal(Identity(user, result.Principal!.GetScopes())), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
});

app.Run();

static ClaimsIdentity Identity(IdentityUser user, IEnumerable<string> scopes)
{
    var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
    identity.SetClaim(Claims.Subject, user.Id).SetClaim(Claims.Email, user.Email).SetClaim(Claims.Name, user.UserName);
    identity.SetScopes(scopes);
    identity.SetDestinations(_ => [Destinations.AccessToken, Destinations.IdentityToken]);
    return identity;
}

public sealed class SpikeDb(DbContextOptions<SpikeDb> options) : IdentityDbContext<IdentityUser>(options);
public partial class Program;
