using System.Security.Claims;
using CSweet.Api.Agents;
using CSweet.Application.Auth;
using CSweet.Contracts.Auth;
using CSweet.Contracts.Localization;
using Microsoft.AspNetCore.Antiforgery;

namespace CSweet.Api.Auth;

public static class AuthenticationEndpoints
{
    public static IEndpointRouteBuilder MapAuthenticationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth");

        group.MapGet("/session", async (
            ClaimsPrincipal principal,
            IAntiforgery antiforgery,
            HttpContext context,
            IAuthenticationService service,
            CancellationToken cancellationToken) =>
        {
            var userId = principal.GetApplicationUserId();
            var token = userId.HasValue ? antiforgery.GetAndStoreTokens(context).RequestToken : null;
            return Results.Ok(await service.GetStatusAsync(userId, token, cancellationToken));
        }).AllowAnonymous();

        group.MapPost("/register", async (RegisterAdminRequest request, IAuthenticationService service, CancellationToken cancellationToken) =>
            ToResult(await service.RegisterAsync(request, cancellationToken), StatusCodes.Status201Created))
            .AllowAnonymous()
            .RequireRateLimiting(AgentRateLimiting.AuthPolicy);

        group.MapPost("/login", async (LoginRequest request, IAuthenticationService service, CancellationToken cancellationToken) =>
            ToResult(await service.LoginAsync(request, cancellationToken)))
            .AllowAnonymous()
            .RequireRateLimiting(AgentRateLimiting.AuthPolicy);

        group.MapPost("/confirm-email", async (ConfirmEmailRequest request, IAuthenticationService service, CancellationToken cancellationToken) =>
            ToResult(await service.ConfirmEmailAsync(request, cancellationToken)))
            .AllowAnonymous()
            .RequireRateLimiting(AgentRateLimiting.AuthPolicy);

        group.MapPost("/resend-confirmation", async (EmailRequest request, IAuthenticationService service, CancellationToken cancellationToken) =>
            ToResult(await service.ResendConfirmationAsync(request, cancellationToken)))
            .AllowAnonymous()
            .RequireRateLimiting(AgentRateLimiting.AuthPolicy);

        group.MapPost("/forgot-password", async (EmailRequest request, IAuthenticationService service, CancellationToken cancellationToken) =>
            ToResult(await service.ForgotPasswordAsync(request, cancellationToken)))
            .AllowAnonymous()
            .RequireRateLimiting(AgentRateLimiting.AuthPolicy);

        group.MapPost("/reset-password", async (ResetPasswordRequest request, IAuthenticationService service, CancellationToken cancellationToken) =>
            ToResult(await service.ResetPasswordAsync(request, cancellationToken)))
            .AllowAnonymous()
            .RequireRateLimiting(AgentRateLimiting.AuthPolicy);

        group.MapPost("/recover-root", async (RecoverRootRequest request, IAuthenticationService service, CancellationToken cancellationToken) =>
            ToResult(await service.RecoverRootAsync(request, cancellationToken)))
            .AllowAnonymous()
            .RequireRateLimiting(AgentRateLimiting.AuthPolicy);

        group.MapPost("/recovery-codes/regenerate", async (
            ClaimsPrincipal principal,
            IAuthenticationService service,
            CancellationToken cancellationToken) =>
        {
            var userId = principal.GetApplicationUserId();
            return userId.HasValue
                ? ToResult(await service.RegenerateRecoveryCodesAsync(userId.Value, cancellationToken))
                : Results.Unauthorized();
        });

        group.MapPost("/logout", async (IAuthenticationService service) =>
        {
            await service.LogoutAsync();
            return Results.NoContent();
        });

        group.MapGet("/preferences", async (
            ClaimsPrincipal principal,
            IAuthenticationService service,
            CancellationToken cancellationToken) =>
        {
            var userId = principal.GetApplicationUserId();
            if (!userId.HasValue)
            {
                return Results.Unauthorized();
            }

            var preferences = await service.GetPreferencesAsync(userId.Value, cancellationToken);
            return preferences is null ? Results.Unauthorized() : Results.Ok(preferences);
        });

        group.MapPut("/preferences", async (
            UpdateUserPreferencesRequest request,
            ClaimsPrincipal principal,
            IAuthenticationService service,
            CancellationToken cancellationToken) =>
        {
            var userId = principal.GetApplicationUserId();
            if (!userId.HasValue)
            {
                return Results.Unauthorized();
            }

            var errors = new Dictionary<string, string[]>();
            string? language = null;
            if (!string.IsNullOrWhiteSpace(request.PreferredLanguage))
            {
                language = SupportedCultures.Normalize(request.PreferredLanguage);
                if (language is null)
                {
                    errors["preferredLanguage"] = ["The language is not supported."];
                }
            }

            string? timeZoneId = null;
            if (!string.IsNullOrWhiteSpace(request.TimeZoneId))
            {
                timeZoneId = request.TimeZoneId.Trim();
                if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _))
                {
                    errors["timeZoneId"] = ["The time zone is not recognized."];
                }
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var preferences = await service.UpdatePreferencesAsync(
                userId.Value,
                new UpdateUserPreferencesRequest(language, timeZoneId),
                cancellationToken);
            return preferences is null ? Results.Unauthorized() : Results.Ok(preferences);
        });

        return endpoints;
    }

    private static IResult ToResult(AuthActionResponse response, int successStatus = StatusCodes.Status200OK)
    {
        if (response.Succeeded)
        {
            return Results.Json(response, statusCode: successStatus);
        }

        var status = response.ErrorCode switch
        {
            "registration_closed" => StatusCodes.Status409Conflict,
            "invalid_credentials" or "email_not_confirmed" or "account_locked" => StatusCodes.Status401Unauthorized,
            "mail_not_configured" or "email_delivery_failed" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest
        };
        return Results.Json(response, statusCode: status);
    }

    public static Guid? GetApplicationUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : null;
    }
}
