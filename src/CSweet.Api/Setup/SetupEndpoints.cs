using CSweet.Office.Contracts.ControlPlane;
using CSweet.Application.Setup;
using CSweet.AI.Providers;
using CSweet.Contracts.Llm;
using CSweet.Contracts.Setup;
using CSweet.Api.Auth;
using System.Security.Claims;

namespace CSweet.Api.Setup;

public static class SetupEndpoints
{
    public static IEndpointRouteBuilder MapSetupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/setup");

        group.MapGet("/status", async (ISetupService setupService, CancellationToken cancellationToken) =>
            Results.Ok(await setupService.GetStatusAsync(cancellationToken)));

        group.MapGet("/execution-capacity", async (
            HttpContext httpContext,
            ISetupService setupService,
            IExecutionFleetService service,
            CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            httpContext.Response.Headers.Pragma = "no-cache";
            httpContext.Response.Headers.Expires = "0";
            await setupService.EnsureSeededAsync(cancellationToken);
            return Results.Ok(await service.GetOnboardingStatusAsync(cancellationToken));
        });

        group.MapPut("/execution-capacity/mode", async (
            SelectExecutionOnboardingModeRequest request,
            IExecutionFleetService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.SelectOnboardingModeAsync(request, cancellationToken);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        }).RequireAuthorization("HostAdministration");

        group.MapPost("/execution-capacity/local-sessions/{sessionId:guid}/launch", async (
            Guid sessionId,
            LaunchLocalOfficeSetupRequest request,
            ClaimsPrincipal principal,
            IExecutionFleetService service,
            CancellationToken cancellationToken) =>
        {
            var userId = principal.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            var result = await service.LaunchLocalSetupSessionAsync(
                sessionId, userId.Value, request, cancellationToken);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        }).RequireAuthorization("HostAdministration");

        group.MapPost("/execution-capacity/enrollments", async (
            IExecutionFleetService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.CreateEnrollmentAsync(cancellationToken);
            return Results.Ok(result);
        }).RequireAuthorization("HostAdministration");

        group.MapPost("/execution-capacity/local-sessions", async (
            CreateLocalOfficeSetupSessionRequest request,
            ClaimsPrincipal principal,
            IExecutionFleetService service,
            CancellationToken cancellationToken) =>
        {
            var userId = principal.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            var result = await service.CreateLocalSetupSessionAsync(request, userId.Value, cancellationToken);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        }).RequireAuthorization("HostAdministration");

        group.MapGet("/execution-capacity/local-sessions/{sessionId:guid}", async (
            Guid sessionId,
            ClaimsPrincipal principal,
            IExecutionFleetService service,
            CancellationToken cancellationToken) =>
        {
            var userId = principal.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            var result = await service.GetLocalSetupSessionAsync(sessionId, userId.Value, cancellationToken);
            return result.Succeeded ? Results.Ok(result) : Results.NotFound(result);
        }).RequireAuthorization("HostAdministration");

        group.MapGet("/execution-capacity/local-sessions/active", async (
            ClaimsPrincipal principal,
            IExecutionFleetService service,
            CancellationToken cancellationToken) =>
        {
            var userId = principal.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            return Results.Ok(await service.GetActiveLocalSetupSessionAsync(userId.Value, cancellationToken));
        }).RequireAuthorization("HostAdministration");

        group.MapPost("/execution-capacity/local-sessions/{sessionId:guid}/handoff", async (
            Guid sessionId,
            ClaimsPrincipal principal,
            IExecutionFleetService service,
            CancellationToken cancellationToken) =>
        {
            var userId = principal.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            var result = await service.RefreshLocalSetupSessionHandoffAsync(
                sessionId, userId.Value, cancellationToken);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        }).RequireAuthorization("HostAdministration");

        group.MapDelete("/execution-capacity/enrollments/{enrollmentId:guid}", async (
            Guid enrollmentId,
            IExecutionFleetService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.RevokeEnrollmentAsync(enrollmentId, cancellationToken);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        }).RequireAuthorization("HostAdministration");

        group.MapPost("/execution-capacity/nodes/{nodeId:guid}/approve", async (
            Guid nodeId,
            IExecutionFleetService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.ApproveNodeAsync(nodeId, cancellationToken);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        }).RequireAuthorization("HostAdministration");

        group.MapPost("/execution-capacity/nodes/{nodeId:guid}/reject", async (
            Guid nodeId,
            IExecutionFleetService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.RejectNodeAsync(nodeId, cancellationToken);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        }).RequireAuthorization("HostAdministration");

        group.MapPost("/steps/{key}/complete", async (string key, ISetupService setupService, CancellationToken cancellationToken) =>
        {
            var result = await setupService.CompleteStepAsync(key, cancellationToken);
            return result.Succeeded
                ? Results.Ok(result)
                : Results.BadRequest(result);
        });

        group.MapPost("/complete", async (ISetupService setupService, CancellationToken cancellationToken) =>
        {
            var result = await setupService.CompleteFirstRunAsync(cancellationToken);
            return result.Succeeded
                ? Results.Ok(result)
                : Results.BadRequest(result);
        });

        group.MapGet("/email-delivery/profiles", async (
            IEmailDeliveryProfileService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(cancellationToken)));

        group.MapGet("/communications/options", (IConfiguration configuration) =>
        {
            var installUrl = configuration["Communications:Discord:InstallUrl"];
            return Results.Ok(new CommunicationSetupOptionsResponse(
                installUrl,
                !string.IsNullOrWhiteSpace(installUrl),
                FirstPartyCommunicationPlugins(configuration)));
        });

        group.MapPost("/email-delivery/profiles", async (
            SaveEmailDeliveryProfileRequest request,
            IEmailDeliveryProfileService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.CreateAsync(request, cancellationToken);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });

        group.MapPut("/email-delivery/profiles/{id:guid}", async (
            Guid id,
            SaveEmailDeliveryProfileRequest request,
            IEmailDeliveryProfileService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.UpdateAsync(id, request, cancellationToken);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });

        group.MapDelete("/email-delivery/profiles/{id:guid}", async (
            Guid id,
            IEmailDeliveryProfileService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.DeleteAsync(id, cancellationToken);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });

        group.MapPost("/email-delivery/profiles/{id:guid}/test", async (
            Guid id,
            ClaimsPrincipal principal,
            IEmailDeliveryProfileService service,
            CancellationToken cancellationToken) =>
        {
            var userId = principal.GetApplicationUserId();
            if (!userId.HasValue) return Results.Unauthorized();
            var result = await service.TestAsync(id, userId.Value, cancellationToken);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });

        group.MapPost("/email-delivery/profiles/{id:guid}/default", async (
            Guid id,
            IEmailDeliveryProfileService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.SetDefaultAsync(id, cancellationToken);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });

        group.MapPost("/default-chat-provider", async (
            SetDefaultChatProviderRequest request,
            ILlmProviderProfileService providerService,
            CancellationToken cancellationToken) =>
        {
            var result = await providerService.SetDefaultChatProviderAsync(request.ProviderProfileId, cancellationToken);
            return result.Succeeded
                ? Results.Ok(result)
                : Results.BadRequest(result);
        });

        return endpoints;
    }

    public static IEndpointRouteBuilder MapOfficeBootstrapEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/offices").AllowAnonymous();
        group.MapPost("/claim", async (
            ClaimOfficeRequest request,
            IExecutionFleetService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.ClaimNodeAsync(request, cancellationToken);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });
        group.MapPost("/local-sessions/redeem", async (
            RedeemAssistedOfficeSetupRequest request,
            IExecutionFleetService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.RedeemLocalSetupSessionAsync(request, cancellationToken);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });
        group.MapPost("/{officeId:guid}/heartbeat", async (
            Guid officeId,
            OfficeHeartbeatRequest request,
            IExecutionFleetService service,
            CancellationToken cancellationToken) =>
            await service.RecordHeartbeatAsync(officeId, request, cancellationToken)
                ? Results.NoContent()
                : Results.Unauthorized());
        return endpoints;
    }

    private static IReadOnlyList<FirstPartyCommunicationPluginResponse> FirstPartyCommunicationPlugins(
        IConfiguration configuration)
    {
        return
        [
            Plugin("discord", "com.csweet.communication.discord", "Discord",
                "Managed servers, channels, direct messages, approvals, and notifications.",
                "https://discord.com/developers/docs/intro",
                "https://discord.com/developers/applications",
                configuration),
            Plugin("slack", "com.csweet.communication.slack", "Slack",
                "Workspace channels, direct messages, app mentions, and interactive approvals.",
                "https://docs.slack.dev/quickstart/",
                "https://api.slack.com/apps",
                configuration),
            Plugin("teams", "com.csweet.communication.teams", "Microsoft Teams",
                "Teams channels, personal chat, notifications, and Microsoft 365 workflows.",
                "https://learn.microsoft.com/en-us/microsoftteams/platform/get-started/get-started-overview",
                "https://dev.teams.microsoft.com/apps",
                configuration),
            Plugin("whatsapp", "com.csweet.communication.whatsapp", "WhatsApp Business",
                "Customer and team conversations through the WhatsApp Cloud API.",
                "https://developers.facebook.com/docs/whatsapp/cloud-api/get-started",
                "https://developers.facebook.com/apps/",
                configuration)
        ];
    }

    private static FirstPartyCommunicationPluginResponse Plugin(
        string key,
        string pluginId,
        string displayName,
        string description,
        string documentationUrl,
        string servicePortalUrl,
        IConfiguration configuration)
    {
        var section = $"Communications:Plugins:{key}";
        return new FirstPartyCommunicationPluginResponse(
            key,
            pluginId,
            displayName,
            description,
            configuration[$"{section}:RepositoryUrl"],
            configuration[$"{section}:CommitSha"],
            documentationUrl,
            servicePortalUrl);
    }
}
