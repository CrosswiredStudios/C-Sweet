using CSweet.Api.Auth;
using CSweet.Application.SourceControl;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.SourceControl;

public static class SourceControlEndpoints
{
    public static IEndpointRouteBuilder MapSourceControlEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/source-control/platform-readiness", async (
                ISourceControlPlatformSetupService service,
                CancellationToken cancellationToken) =>
                Results.Ok(await service.GetReadinessAsync(cancellationToken)))
            .RequireAuthorization("SourceControlAdministration");

        var platform = endpoints.MapGroup("/api/source-control/platform-setup")
            .RequireAuthorization("SourceControlAdministration");

        platform.MapGet("/", async (
            HttpContext http,
            ISourceControlPlatformSetupService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(http, userId => service.GetAsync(userId, cancellationToken)));

        platform.MapPost("/sessions", async (
            StartPlatformSourceControlSetupRequest request,
            HttpContext http,
            ISourceControlPlatformSetupService service,
            CancellationToken cancellationToken) =>
        {
            if (!MatchesPublicRequestBase(http, request.PublicBaseUrl))
                return Results.BadRequest(new { error = "public_base_url_mismatch", message = "The public application URL did not match this request." });
            var callbackBase = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}".TrimEnd('/');
            return await ExecuteAsync(http, userId => service.StartAsync(
                userId, request with { ManifestCallbackUrl = callbackBase }, cancellationToken));
        });

        platform.MapPut("/sessions/{sessionId:guid}/organization", async (
            Guid sessionId,
            ConfirmPlatformOrganizationRequest request,
            HttpContext http,
            ISourceControlPlatformSetupService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(http, userId => service.ConfirmOrganizationAsync(
                userId, sessionId, request, cancellationToken)));

        platform.MapPut("/sessions/{sessionId:guid}/apps/{kind}/review", async (
            Guid sessionId,
            string kind,
            ConfirmPlatformAppReviewRequest request,
            HttpContext http,
            ISourceControlPlatformSetupService service,
            CancellationToken cancellationToken) =>
            await ExecuteWithKindAsync(http, kind, appKind => service.ConfirmReviewAsync(
                http.User.GetApplicationUserId()!.Value, sessionId, appKind, request, cancellationToken)));

        platform.MapPost("/sessions/{sessionId:guid}/apps/{kind}/manifest", async (
            Guid sessionId,
            string kind,
            HttpContext http,
            ISourceControlPlatformSetupService service,
            CancellationToken cancellationToken) =>
            await ExecuteWithKindAsync(http, kind, appKind => service.CreateManifestAsync(
                http.User.GetApplicationUserId()!.Value, sessionId, appKind, cancellationToken)));

        platform.MapPut("/sessions/{sessionId:guid}/apps/{kind}/confirm", async (
            Guid sessionId,
            string kind,
            ConfirmPlatformAppRequest request,
            HttpContext http,
            ISourceControlPlatformSetupService service,
            CancellationToken cancellationToken) =>
            await ExecuteWithKindAsync(http, kind, appKind => service.ConfirmAppAsync(
                http.User.GetApplicationUserId()!.Value, sessionId, appKind, request, cancellationToken)));

        platform.MapPut("/sessions/{sessionId:guid}/provisioner-choice", async (
            Guid sessionId,
            ChoosePlatformProvisionerRequest request,
            HttpContext http,
            ISourceControlPlatformSetupService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(http, userId => service.ChooseProvisionerAsync(
                userId, sessionId, request, cancellationToken)));

        platform.MapPost("/sessions/{sessionId:guid}/activate", async (
            Guid sessionId,
            ActivatePlatformSourceControlRequest request,
            HttpContext http,
            ISourceControlPlatformSetupService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(http, userId => service.ActivateAsync(
                userId, sessionId, request, cancellationToken)));

        platform.MapPost("/sessions/{sessionId:guid}/cancel", async (
            Guid sessionId,
            CancelPlatformSourceControlSetupRequest request,
            HttpContext http,
            ISourceControlPlatformSetupService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(http, userId => service.CancelAsync(
                userId, sessionId, request, cancellationToken)));

        platform.MapGet("/github-manifest-callback", async (
            string? code,
            string? state,
            HttpContext http,
            ISourceControlPlatformSetupService service,
            CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue || string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
                return Results.Redirect("/settings/source-control?githubSetup=failed");
            try
            {
                var completion = await service.CompleteManifestAsync(
                    userId.Value, code, state, cancellationToken);
                return Results.Redirect($"{completion.PublicBaseUrl}/settings/source-control?githubSetup=verified&session={completion.SessionId:D}");
            }
            catch
            {
                var setup = await service.GetAsync(userId.Value, CancellationToken.None);
                var returnBase = setup.Session?.PublicBaseUrl;
                return Results.Redirect(string.IsNullOrWhiteSpace(returnBase)
                    ? "/settings/source-control?githubSetup=failed"
                    : $"{returnBase}/settings/source-control?githubSetup=failed");
            }
        });

        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/source-control")
            .RequireAuthorization();

        group.MapGet("/dashboard", async (
            Guid organizationId,
            HttpContext http,
            ISourceControlOnboardingService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(http, userId => service.GetDashboardAsync(
                organizationId, userId, cancellationToken)));

        group.MapPost("/onboarding", async (
            Guid organizationId,
            StartSourceControlOnboardingRequest request,
            HttpContext http,
            ISourceControlOnboardingService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(http, userId => service.StartAsync(
                organizationId, userId, request, cancellationToken)));

        group.MapPost("/onboarding/{sessionId:guid}/github-installation", async (
            Guid organizationId,
            Guid sessionId,
            CompleteGitHubAppInstallationRequest request,
            HttpContext http,
            ISourceControlOnboardingService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(http, userId => service.CompleteGitHubInstallationAsync(
                organizationId, userId, sessionId, request, cancellationToken)));

        group.MapGet("/connections/{connectionId:guid}/available-projects", async (
            Guid organizationId,
            Guid connectionId,
            bool templates,
            HttpContext http,
            ISourceControlOnboardingService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(http, userId => service.ListAvailableRepositoriesAsync(
                organizationId, userId, connectionId, templates, cancellationToken)));

        group.MapPut("/connections/{connectionId:guid}/existing-projects", async (
            Guid organizationId,
            Guid connectionId,
            SelectExistingCodeProjectsRequest request,
            HttpContext http,
            ISourceControlOnboardingService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(http, userId => service.SelectExistingRepositoriesAsync(
                organizationId, userId, connectionId, request, cancellationToken)));

        group.MapPut("/connections/{connectionId:guid}/managed-policy", async (
            Guid organizationId,
            Guid connectionId,
            ConfigureManagedCodeProjectsRequest request,
            HttpContext http,
            ISourceControlOnboardingService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(http, userId => service.ConfigureManagedRepositoriesAsync(
                organizationId, userId, connectionId, request, cancellationToken)));

        group.MapPost("/approvals/{approvalId:guid}/decision", async (
            Guid organizationId,
            Guid approvalId,
            DecideSourceControlApprovalRequest request,
            HttpContext http,
            ISourceControlApprovalService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(http, userId => service.DecideAsync(
                organizationId, userId, approvalId, request, cancellationToken)));

        return endpoints;
    }

    private static async Task<IResult> ExecuteAsync<T>(
        HttpContext http,
        Func<Guid, Task<T>> action)
    {
        var userId = http.User.GetApplicationUserId();
        if (!userId.HasValue)
            return Results.Unauthorized();
        try
        {
            return Results.Ok(await action(userId.Value));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Results.Json(
                new { error = "not_authorized", message = exception.Message },
                statusCode: StatusCodes.Status403Forbidden);
        }
        catch (KeyNotFoundException exception)
        {
            return Results.NotFound(new { error = "not_found", message = exception.Message });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Results.BadRequest(new { error = "source_control_setup_failed", message = exception.Message });
        }
        catch (DbUpdateConcurrencyException exception)
        {
            return Results.Conflict(new { error = "revision_conflict", message = exception.Message });
        }
    }

    private static async Task<IResult> ExecuteWithKindAsync<T>(
        HttpContext http,
        string kind,
        Func<PlatformGitHubAppKind, Task<T>> action)
    {
        if (!http.User.GetApplicationUserId().HasValue)
            return Results.Unauthorized();
        if (!TryParseKind(kind, out var appKind))
            return Results.BadRequest(new { error = "invalid_app_kind", message = "Choose Source Access or Repository Provisioner." });
        return await ExecuteAsync(http, _ => action(appKind));
    }

    private static bool TryParseKind(string value, out PlatformGitHubAppKind kind)
    {
        if (string.Equals(value, "source-access", StringComparison.OrdinalIgnoreCase))
        {
            kind = PlatformGitHubAppKind.SourceAccess;
            return true;
        }
        if (string.Equals(value, "provisioner", StringComparison.OrdinalIgnoreCase))
        {
            kind = PlatformGitHubAppKind.Provisioner;
            return true;
        }
        kind = default;
        return false;
    }

    private static bool MatchesPublicRequestBase(HttpContext http, string supplied)
    {
        if (!Uri.TryCreate(supplied.TrimEnd('/'), UriKind.Absolute, out var suppliedUri))
            return false;
        var origin = http.Request.Headers.Origin.ToString();
        var requestBase = !string.IsNullOrWhiteSpace(origin)
            ? origin
            : $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";
        if (!Uri.TryCreate(requestBase.TrimEnd('/'), UriKind.Absolute, out var requestUri))
            return false;
        return string.Equals(suppliedUri.Scheme, requestUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(suppliedUri.Authority, requestUri.Authority, StringComparison.OrdinalIgnoreCase) &&
               (!string.IsNullOrWhiteSpace(origin) ||
                string.Equals(suppliedUri.AbsolutePath.TrimEnd('/'), requestUri.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal));
    }
}
