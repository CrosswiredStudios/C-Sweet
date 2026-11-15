using System.Security.Claims;
using CSweet.Api.Agents;
using CSweet.Application.Auth;
using CSweet.Contracts.Auth;
using CSweet.Domain.Setup;
using CSweet.HostedAccount;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.Api.Auth;

/// <summary>Raised when the active installation link changes, so the relay connector reattaches with the new identity.</summary>
public sealed class InstallationLinkSignal
{
    private CancellationTokenSource _changed = new();

    public CancellationToken Token => Volatile.Read(ref _changed).Token;

    public void Notify()
    {
        var previous = Interlocked.Exchange(ref _changed, new CancellationTokenSource());
        previous.Cancel();
        previous.Dispose();
    }
}

/// <summary>
/// Settings → C-Sweet account (IL-11, first cut): link an existing installation to the configured issuer through the
/// same Market approval as hosted first run, or move it from one issuer to another (for example from a local Market
/// to account.c-sweet.com). Administrators only; the attempt lives in an HttpOnly cookie scoped to these routes.
/// Unlinking (Disconnect) is later work.
/// </summary>
public static class InstallationLinkEndpoints
{
    private const string CookieName = "CSweet.InstallationLink";
    private const string CookiePath = "/api/installation/link";

    public static IEndpointRouteBuilder MapInstallationLinkEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(CookiePath).RequireAuthorization("HostAdministration");

        group.MapGet("", async (CSweetDbContext db, IOptions<HostedAccountOptions> options, CancellationToken ct) =>
        {
            var configured = options.Value.Issuer;
            var link = await db.InstallationLinks.AsNoTracking().Where(x => x.State == InstallationLinkState.Active)
                .OrderByDescending(x => x.LinkedAt).FirstOrDefaultAsync(ct);
            return Results.Ok(link is null
                ? new InstallationLinkStatusResponse(options.Value.LinkingEnabled, configured, false, false)
                : new InstallationLinkStatusResponse(options.Value.LinkingEnabled, configured, true, SameIssuer(link.Issuer, configured),
                    link.Issuer, link.InstallationId, link.DisplayName, link.LinkedAt));
        });

        group.MapPost("/start", async (StartHostedSetupRequest request, HttpContext http, IOptions<HostedAccountOptions> options,
            HostedSetupCoordinator setup, CancellationToken ct) =>
        {
            if (!options.Value.LinkingEnabled) return Results.NotFound();
            if (http.Request.Cookies.TryGetValue(CookieName, out var previous)) setup.Remove(previous);
            try
            {
                var snapshot = await setup.StartAsync(request.InstallationName, ct);
                http.Response.Cookies.Append(CookieName, snapshot.AttemptId, Cookie(http, snapshot.ExpiresAt));
                return Results.Ok(HostedSetupEndpoints.Map(snapshot));
            }
            catch (HostedAccountException exception)
            {
                return Results.Ok(new HostedSetupStatusResponse("unavailable", Message: exception.Message));
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return Results.Ok(new HostedSetupStatusResponse("unavailable", Message: "C-Sweet can't be reached right now."));
            }
        }).RequireRateLimiting(AgentRateLimiting.AuthPolicy);

        group.MapPost("/poll", async (HttpContext http, HostedSetupCoordinator setup, CancellationToken ct) =>
        {
            if (!http.Request.Cookies.TryGetValue(CookieName, out var attemptId)) return Results.Ok(new HostedSetupStatusResponse("missing"));
            var snapshot = await setup.PollAsync(attemptId, ct);
            return Results.Ok(snapshot is null ? new HostedSetupStatusResponse("missing") : HostedSetupEndpoints.Map(snapshot));
        });

        group.MapPost("/complete", async (CompleteInstallationLinkRequest request, HttpContext http, ClaimsPrincipal user,
            IAuthenticationService auth, HostedSetupCoordinator setup, DirectoryPublicationSignal directory,
            InstallationLinkSignal linkChanged, CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var administratorId)) return Results.Unauthorized();
            if (!http.Request.Cookies.TryGetValue(CookieName, out var attemptId) || setup.GetApproval(attemptId) is not { } approval)
            {
                return Results.Json(new AuthActionResponse(false, "hosted_not_approved", "Approve this Headquarters on C-Sweet first."),
                    statusCode: StatusCodes.Status409Conflict);
            }
            var result = await auth.LinkExistingAsync(administratorId, request.Password, new HostedAdministratorLink(
                approval.Identity.Issuer, approval.Identity.Subject, approval.Identity.Email,
                approval.InstallationId, approval.InstallationName, approval.PrivateKey), ct);
            if (!result.Succeeded) return AuthenticationEndpoints.ToResult(result);
            setup.Remove(attemptId);
            http.Response.Cookies.Delete(CookieName, new CookieOptions { Path = CookiePath });
            directory.Notify();
            linkChanged.Notify();
            return Results.Ok(result);
        }).RequireRateLimiting(AgentRateLimiting.AuthPolicy);

        group.MapPost("/cancel", (HttpContext http, HostedSetupCoordinator setup) =>
        {
            if (http.Request.Cookies.TryGetValue(CookieName, out var attemptId)) setup.Remove(attemptId);
            http.Response.Cookies.Delete(CookieName, new CookieOptions { Path = CookiePath });
            return Results.NoContent();
        });

        return endpoints;
    }

    internal static bool SameIssuer(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static CookieOptions Cookie(HttpContext http, DateTimeOffset expires) => new()
    {
        HttpOnly = true,
        Secure = http.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = CookiePath,
        Expires = expires,
        IsEssential = true
    };
}
