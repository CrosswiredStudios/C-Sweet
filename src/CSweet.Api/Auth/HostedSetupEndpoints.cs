using CSweet.Application.Auth;
using CSweet.Contracts.Auth;
using CSweet.Api.Agents;
using CSweet.HostedAccount;
using Microsoft.Extensions.Options;

namespace CSweet.Api.Auth;

/// <summary>
/// Hosted first run (H2): get a code, approve it on C-Sweet, then create the administrator. The attempt id lives
/// only in an HttpOnly cookie scoped to these routes, so only the browser that started setup can finish it.
/// Every route answers 404 unless registration is still open and <c>Installation:LinkingEnabled</c> is on.
/// </summary>
public static class HostedSetupEndpoints
{
    private const string CookieName = "CSweet.HostedSetup";
    private const string CookiePath = "/api/auth/hosted";

    public static IEndpointRouteBuilder MapHostedSetupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(CookiePath).AllowAnonymous();

        group.MapPost("/start", async (StartHostedSetupRequest request, HttpContext http, IAuthenticationService auth,
            IOptions<HostedAccountOptions> options, HostedSetupCoordinator setup, CancellationToken ct) =>
        {
            if (!await IsAvailableAsync(auth, options.Value, ct)) return Results.NotFound();
            if (http.Request.Cookies.TryGetValue(CookieName, out var previous)) setup.Remove(previous);
            try
            {
                var snapshot = await setup.StartAsync(request.InstallationName, ct);
                http.Response.Cookies.Append(CookieName, snapshot.AttemptId, Cookie(http, snapshot.ExpiresAt));
                return Results.Ok(Map(snapshot));
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

        group.MapPost("/poll", async (HttpContext http, IAuthenticationService auth, IOptions<HostedAccountOptions> options,
            HostedSetupCoordinator setup, CancellationToken ct) =>
        {
            if (!await IsAvailableAsync(auth, options.Value, ct)) return Results.NotFound();
            if (!http.Request.Cookies.TryGetValue(CookieName, out var attemptId)) return Results.Ok(new HostedSetupStatusResponse("missing"));
            var snapshot = await setup.PollAsync(attemptId, ct);
            return Results.Ok(snapshot is null ? new HostedSetupStatusResponse("missing") : Map(snapshot));
        });

        group.MapPost("/complete", async (CompleteHostedSetupRequest request, HttpContext http, IAuthenticationService auth,
            IOptions<HostedAccountOptions> options, HostedSetupCoordinator setup, CancellationToken ct) =>
        {
            if (!await IsAvailableAsync(auth, options.Value, ct)) return Results.NotFound();
            if (!http.Request.Cookies.TryGetValue(CookieName, out var attemptId) || setup.GetApproval(attemptId) is not { } approval)
            {
                return Results.Json(new AuthActionResponse(false, "hosted_not_approved", "Approve this Headquarters on C-Sweet first."),
                    statusCode: StatusCodes.Status409Conflict);
            }

            var registration = AuthenticationEndpoints.WithInitialLocale(
                new RegisterAdminRequest(request.DisplayName, approval.Identity.Email, request.Password, request.ConfirmPassword,
                    request.PreferredLanguage, request.TimeZoneId),
                http.Request.Headers.AcceptLanguage.ToString());
            var result = await auth.RegisterHostedAsync(registration, new HostedAdministratorLink(
                approval.Identity.Issuer, approval.Identity.Subject, approval.Identity.Email,
                approval.InstallationId, approval.InstallationName, approval.PrivateKey), ct);
            if (result.Succeeded)
            {
                setup.Remove(attemptId);
                http.Response.Cookies.Delete(CookieName, new CookieOptions { Path = CookiePath });
            }
            return AuthenticationEndpoints.ToResult(result, StatusCodes.Status201Created);
        }).RequireRateLimiting(AgentRateLimiting.AuthPolicy);

        group.MapPost("/cancel", (HttpContext http, HostedSetupCoordinator setup) =>
        {
            if (http.Request.Cookies.TryGetValue(CookieName, out var attemptId)) setup.Remove(attemptId);
            http.Response.Cookies.Delete(CookieName, new CookieOptions { Path = CookiePath });
            return Results.NoContent();
        });

        return endpoints;
    }

    private static async Task<bool> IsAvailableAsync(IAuthenticationService auth, HostedAccountOptions options, CancellationToken ct) =>
        options.LinkingEnabled && (await auth.GetStatusAsync(null, cancellationToken: ct)).RegistrationOpen;

    private static CookieOptions Cookie(HttpContext http, DateTimeOffset expires) => new()
    {
        HttpOnly = true,
        Secure = http.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = CookiePath,
        Expires = expires,
        IsEssential = true
    };

    private static HostedSetupStatusResponse Map(HostedSetupSnapshot snapshot) => new(
        snapshot.State switch
        {
            HostedSetupState.Waiting => "waiting",
            HostedSetupState.Approved => "approved",
            HostedSetupState.Denied => "denied",
            HostedSetupState.Expired => "expired",
            _ => "failed"
        },
        snapshot.UserCode,
        snapshot.VerificationUriComplete.AbsoluteUri,
        snapshot.CreateAccountUri.AbsoluteUri,
        snapshot.ExpiresAt,
        Math.Max(1, (int)Math.Ceiling(snapshot.PollInterval.TotalSeconds)),
        snapshot.InstallationName,
        snapshot.Email,
        snapshot.Error);
}
