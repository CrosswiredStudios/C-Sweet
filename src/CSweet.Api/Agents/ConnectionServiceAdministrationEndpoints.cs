using System.Security.Claims;
using CSweet.ConnectionService.Protocol;
using CSweet.Contracts.Plugins;
using CSweet.Infrastructure.Auth;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;

namespace CSweet.Api.Agents;

public static class ConnectionServiceAdministrationEndpoints
{
    public sealed record OwnerAction<T>(long GrantRevision, T Request);
    public static IEndpointRouteBuilder MapConnectionServiceAdministrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/plugins/connection-service")
            .RequireAuthorization(p => p.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme).RequireAuthenticatedUser());
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var identity = http.User.Identities.Where(x => x.IsAuthenticated && x.AuthenticationType == IdentityConstants.ApplicationScheme).ToArray();
            if (identity.Length != 1 || identity[0].FindAll(ClaimTypes.NameIdentifier).Count() != 1) return Results.Forbid();
            var signIn = http.RequestServices.GetRequiredService<SignInManager<ApplicationUser>>();
            var users = http.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await signIn.ValidateSecurityStampAsync(http.User);
            if (user is null || !await signIn.CanSignInAsync(user) || await users.IsLockedOutAsync(user) || !await users.IsInRoleAsync(user, AuthenticationService.AdministratorRole)) return Results.Forbid();
            if (!HttpMethods.IsGet(http.Request.Method))
            {
                try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
                catch (AntiforgeryValidationException) { return Results.BadRequest(new { error = "invalid_antiforgery_token" }); }
            }
            http.Items["connection-service-actor"] = user.Id;
            http.Response.Headers.CacheControl = "no-store";
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try { return await next(context); }
            catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or ArgumentException or System.Security.Cryptography.CryptographicException or IOException or System.Text.Json.JsonException or HttpRequestException or Microsoft.EntityFrameworkCore.DbUpdateException)
            {
                OwnerFailureReporting.Report(http, error, started, http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("CSweet.OwnerControl"));
                return Results.BadRequest(new { error = "connection_service_operation_unavailable", message = "Check the selected service, current permissions and local bootstrap. A submitted change may have succeeded; check service state before retrying." });
            }
        });
        static Guid Actor(HttpContext http) => (Guid)http.Items["connection-service-actor"]!;
        group.MapGet("/selection", (ConnectionServiceSelectionStore store) => store.Current());
        group.MapPut("/selection", (HttpContext http, SaveConnectionServiceChoice request, ConnectionServiceSelectionStore store, CancellationToken ct) => store.Save(Actor(http), request, ct));
        group.MapPost("/probe", (ConnectionServiceSelectionStore store, CancellationToken ct) => store.Probe(ct));
        group.MapPost("/owner/prepare", (HttpContext http, ConnectionServiceOwnerGateway gateway, CancellationToken ct) => gateway.Prepare(Actor(http), ct));
        group.MapPost("/owner/access", (HttpContext http, ConnectionServiceOwnerGateway gateway, CancellationToken ct) => gateway.Access(Actor(http), ct));
        group.MapPost("/owner/import", (HttpContext http, OwnerAction<OwnerCredentialImport> request, ConnectionServiceOwnerGateway gateway, CancellationToken ct) => gateway.Import(Actor(http), request.GrantRevision, request.Request, ct));
        group.MapPost("/owner/approve", (HttpContext http, OwnerAction<ApprovePackage> request, ConnectionServiceOwnerGateway gateway, CancellationToken ct) => gateway.Approve(Actor(http), request.GrantRevision, request.Request, ct));
        group.MapPost("/owner/enroll", (HttpContext http, OwnerAction<OwnerEnrollment> request, ConnectionServiceOwnerGateway gateway, CancellationToken ct) => gateway.Enroll(Actor(http), request.GrantRevision, request.Request, ct));
        group.MapPost("/owner/revoke", (HttpContext http, OwnerAction<OwnerRevocation> request, ConnectionServiceOwnerGateway gateway, CancellationToken ct) => gateway.Revoke(Actor(http), request.GrantRevision, request.Request, ct));
        return endpoints;
    }
}
