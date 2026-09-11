using System.Net;
using System.Security.Cryptography;
using CSweet.Api.Auth;
using CSweet.Infrastructure.Setup;

namespace CSweet.Api.Core;

public static class WebPreviewAccessEndpoints
{
    public static void MapWebPreviewAccessEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/core/organizations/{organizationId:guid}/web-previews/availability", async (Guid organizationId, HttpContext http, WebPreviewManagementService service, CancellationToken token) =>
        {
            if (http.User.GetApplicationUserId() is not { } userId) return Results.Forbid();
            try { return Results.Ok(new { enabled = await service.EnabledAsync(organizationId, userId, token) }); }
            catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException) { return Results.Forbid(); }
        }).RequireAuthorization();
        endpoints.MapGet("/api/core/organizations/{organizationId:guid}/web-previews", async (Guid organizationId, HttpContext http, WebPreviewManagementService service, CancellationToken token) =>
        {
            if (http.User.GetApplicationUserId() is not { } userId) return Results.Forbid();
            try { return Results.Ok(await service.ReadAsync(organizationId, userId, token)); }
            catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException) { return Results.Forbid(); }
        }).RequireAuthorization();
        endpoints.MapPost("/api/core/organizations/{organizationId:guid}/web-previews/{previewId:guid}/stop", async (Guid organizationId, Guid previewId, HttpContext http, WebPreviewManagementService service, CancellationToken token) =>
        {
            if (http.User.GetApplicationUserId() is not { } userId) return Results.Forbid();
            try { await service.StopAsync(organizationId, userId, previewId, token); return Results.NoContent(); }
            catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException) { return Results.Forbid(); }
        }).RequireAuthorization();
        endpoints.MapPut("/api/core/organizations/{organizationId:guid}/web-previews/triage/{projectId:guid}",
            async (Guid organizationId, Guid projectId, CSweet.WebHost.Contracts.ConfigurePreviewTriage input, HttpContext http, WebPreviewTriageService triage, CancellationToken token) =>
        {
            if (http.User.GetApplicationUserId() is not { } userId) return Results.Forbid();
            try { await triage.ConfigureAsync(organizationId, userId, projectId, input, token); return Results.NoContent(); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return Results.Conflict(new { code = "TriageConfigurationUnavailable" }); }
        }).RequireAuthorization();
        endpoints.MapGet("/api/core/organizations/{organizationId:guid}/web-previews/{previewId:guid}/open",
            async (Guid organizationId, Guid previewId, HttpContext http, WebPreviewGatewayService gateway, CancellationToken token) =>
        {
            if (http.User.GetApplicationUserId() is not { } userId) return Results.Forbid();
            try
            {
                var ticket = await gateway.OpenAsync(organizationId, userId, previewId, token);
                var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
                http.Response.Headers.CacheControl = "no-store";
                http.Response.Headers["Referrer-Policy"] = "no-referrer";
                http.Response.Headers["Content-Security-Policy"] = $"default-src 'none'; script-src 'nonce-{nonce}'; form-action {ticket.Origin}; frame-ancestors 'none'; base-uri 'none'";
                return Results.Content($"<!doctype html><meta charset=utf-8><title>Opening private preview</title><form method=post action=\"{WebUtility.HtmlEncode(ticket.Origin)}/__preview/session\"><input type=hidden name=ticket value=\"{WebUtility.HtmlEncode(ticket.Ticket)}\"><button>Open private preview</button></form><script nonce=\"{nonce}\">document.forms[0].submit()</script>", "text/html");
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (InvalidOperationException) { return Results.Conflict(new { code = "PreviewAccessUnavailable" }); }
        }).RequireAuthorization();
    }
}
