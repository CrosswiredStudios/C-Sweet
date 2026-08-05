using CSweet.Api.Auth;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Infrastructure.Setup;
using Microsoft.Extensions.Options;
using CSweet.Infrastructure.Persistence;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Agents;

public static class PluginSetupEndpoints
{
    public static IEndpointRouteBuilder MapPluginSetupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/core/organizations/{organizationId:guid}/plugin-setup");
        group.AddEndpointFilter(async (context, next) =>
        {
            var applicationUserId = context.HttpContext.User.GetApplicationUserId();
            var organizationText = context.HttpContext.Request.RouteValues["organizationId"]?.ToString();
            if (!applicationUserId.HasValue || !Guid.TryParse(organizationText, out var organizationId))
                return Results.Forbid();
            var db = context.HttpContext.RequestServices.GetRequiredService<CSweetDbContext>();
            var authorized = await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
                x.OrganizationId == organizationId && x.ApplicationUserId == applicationUserId.Value &&
                x.IsActive && x.PermissionLevel >= OrganizationPermissionLevel.Manager,
                context.HttpContext.RequestAborted);
            return authorized ? await next(context) : Results.Forbid();
        });
        group.MapGet("/{installationId:guid}", async (Guid organizationId, Guid installationId,
            IPluginSetupService setup, CancellationToken cancellationToken) =>
            Results.Ok(await setup.GetAsync(organizationId, installationId, cancellationToken)));

        group.MapPost("/{installationId:guid}/steps/{stepId}/complete", async (Guid organizationId,
            Guid installationId, string stepId, CompletePluginSetupStepRequest request,
            IPluginSetupService setup, CancellationToken cancellationToken) =>
            Results.Ok(await setup.CompleteStepAsync(organizationId, installationId, stepId, request, cancellationToken)));

        group.MapPost("/{installationId:guid}/steps/{stepId}/invoke", async (Guid organizationId,
            Guid installationId, string stepId, System.Text.Json.JsonElement arguments, HttpContext http,
            IPluginBootstrapCapabilityService bootstrap, CSweetDbContext db, CancellationToken cancellationToken) =>
        {
            var applicationUserId = http.User.GetApplicationUserId();
            if (!applicationUserId.HasValue || !await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
                    x.OrganizationId == organizationId && x.ApplicationUserId == applicationUserId.Value &&
                    x.IsActive && x.PermissionLevel >= OrganizationPermissionLevel.Manager, cancellationToken))
                return Results.Forbid();
            try
            {
                return Results.Ok(new PluginBootstrapCallbackResponse(await bootstrap.InvokeAsync(
                    organizationId, installationId, stepId, arguments, cancellationToken)));
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
            {
                return Results.BadRequest(new { error = "bootstrap_callback_failed", message = exception.Message });
            }
        });

        group.MapPost("/{installationId:guid}/connections/{connectionId}/authorize", async (
            Guid organizationId, Guid installationId, string connectionId, BeginPluginAuthorizationRequest request,
            HttpContext http, IPluginSetupService setup, IOptions<PluginConnectionOptions> options,
            CancellationToken cancellationToken) =>
        {
            var applicationUserId = http.User.GetApplicationUserId();
            if (!applicationUserId.HasValue) return Results.Forbid();
            var baseUrl = options.Value.PublicBaseUrl?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl))
                return Results.Problem("CSweet:PluginConnections:PublicBaseUrl must be configured.", statusCode: 503);
            var redirectUri = $"{baseUrl}/api/plugin-connections/oauth/callback";
            return Results.Ok(await setup.BeginAuthorizationAsync(organizationId, applicationUserId.Value,
                installationId, connectionId, request, redirectUri, cancellationToken));
        });

        group.MapPost("/{installationId:guid}/activate", async (Guid organizationId, Guid installationId,
            HttpContext http, IPluginSetupService setup, CancellationToken cancellationToken) =>
        {
            var applicationUserId = http.User.GetApplicationUserId();
            return applicationUserId.HasValue
                ? Results.Ok(await setup.ActivateAsync(organizationId, applicationUserId.Value, installationId, cancellationToken))
                : Results.Forbid();
        });

        group.MapDelete("/{installationId:guid}/connections/{connectionId}", async (Guid organizationId,
            Guid installationId, string connectionId, IPluginSetupService setup, CancellationToken cancellationToken) =>
        {
            await setup.DisconnectAsync(organizationId, installationId, connectionId, cancellationToken);
            return Results.NoContent();
        });

        group.MapGet("/{installationId:guid}/standing-policy", async (Guid organizationId,
            Guid installationId, IPluginStandingPolicyService policies, CancellationToken cancellationToken) =>
        {
            var policy = await policies.GetAsync(organizationId, installationId, cancellationToken);
            return policy is null ? Results.NotFound() : Results.Ok(policy);
        });

        group.MapPut("/{installationId:guid}/standing-policy", async (Guid organizationId,
            Guid installationId, ApprovePluginStandingPolicyRequest request, HttpContext http,
            IPluginStandingPolicyService policies, CancellationToken cancellationToken) =>
        {
            var applicationUserId = http.User.GetApplicationUserId();
            if (!applicationUserId.HasValue) return Results.Forbid();
            try
            {
                return Results.Ok(await policies.ApproveAsync(organizationId, applicationUserId.Value,
                    installationId, request, cancellationToken));
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            {
                return Results.BadRequest(new { error = "standing_policy_invalid", message = exception.Message });
            }
        });

        group.MapDelete("/{installationId:guid}/standing-policy", async (Guid organizationId,
            Guid installationId, HttpContext http, IPluginStandingPolicyService policies,
            CancellationToken cancellationToken) =>
        {
            var applicationUserId = http.User.GetApplicationUserId();
            if (!applicationUserId.HasValue) return Results.Forbid();
            try
            {
                await policies.RevokeAsync(organizationId, applicationUserId.Value, installationId,
                    cancellationToken);
                return Results.NoContent();
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });

        group.MapGet("/{installationId:guid}/secrets/{reference}", async (Guid organizationId,
            Guid installationId, string reference, HttpContext http, CSweetDbContext db,
            IPluginSecretStore secrets, IAuditEventWriter audit, CancellationToken cancellationToken) =>
        {
            var applicationUserId = http.User.GetApplicationUserId();
            var authorized = applicationUserId.HasValue && await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
                x.OrganizationId == organizationId && x.ApplicationUserId == applicationUserId.Value &&
                x.IsActive && x.PermissionLevel == OrganizationPermissionLevel.Owner, cancellationToken);
            if (!authorized || !reference.StartsWith("plugin-secret:", StringComparison.Ordinal) ||
                !await db.AgentInstallations.AsNoTracking().AnyAsync(x =>
                    x.Id == installationId && x.BusinessId == organizationId.ToString("D"), cancellationToken))
                return Results.Forbid();
            var suffix = reference[14..];
            if (suffix.Length != 32 || suffix.Any(x => !Uri.IsHexDigit(x))) return Results.BadRequest();
            var value = await secrets.GetAsync(installationId, $"response.{suffix}", cancellationToken);
            if (value is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "no-store";
            await audit.WriteAsync("plugin.response-secret.revealed", "PluginInstallation", installationId,
                "An organization owner revealed a provider response secret through the protected platform control.",
                null, cancellationToken);
            return Results.Ok(new { value });
        });

        endpoints.MapGet("/api/plugin-connections/oauth/callback", async (string? code, string? state,
            string? error, HttpContext http, IPluginSetupService setup, CancellationToken cancellationToken) =>
        {
            if (!string.IsNullOrWhiteSpace(error)) return Results.BadRequest("Provider authorization was not completed.");
            var applicationUserId = http.User.GetApplicationUserId();
            if (!applicationUserId.HasValue || string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
                return Results.BadRequest("The authorization response is incomplete.");
            var completion = await setup.CompleteAuthorizationAsync(applicationUserId.Value, code, state, cancellationToken);
            return Results.Redirect($"/organizations/{completion.OrganizationId:D}/plugin-setup/{completion.InstallationId:D}");
        });
        return endpoints;
    }
}
