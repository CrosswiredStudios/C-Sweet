using System.Text.Json;
using CSweet.Api.Auth;
using CSweet.Application.Setup;
using CSweet.Infrastructure.Setup;
using CSweet.WebHost.Contracts;
using Microsoft.EntityFrameworkCore;
namespace CSweet.Api.Core;

public static class WebHostEndpoints
{
    public static IEndpointRouteBuilder MapWebHostEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var owners = endpoints.MapGroup("/api/core/organizations/{organizationId:guid}/web-hosts").RequireAuthorization();
        owners.MapGet("/", async (Guid organizationId, HttpContext http, WebHostRegistryService service, CancellationToken token) =>
        {
            if (http.User.GetApplicationUserId() is not { } userId) return Results.Forbid();
            try { return Results.Ok(await service.ListAsync(organizationId, userId, token)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Results.StatusCode(408); }
        });
        owners.MapPost("/", async (Guid organizationId, HttpContext http, WebHostRegistryService service,
            IAuditEventWriter audit, CancellationToken token) =>
        {
            if (http.User.GetApplicationUserId() is not { } userId) return Results.Forbid();
            try
            {
                var request = await ReadAsync<RegisterWebHost>(http.Request, 16384, token);
                var bootstrap = await service.RegisterAsync(organizationId, userId, request, token);
                await audit.WriteAsync("web-host.registered", "WebHost", bootstrap.Enrollment.Id,
                    "An independent product host identity was registered.", cancellationToken: token);
                return Results.Ok(bootstrap);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Results.StatusCode(408); }
            catch (Exception error) when (error is ArgumentException or JsonException or InvalidDataException or
                FormatException or System.Security.Cryptography.CryptographicException)
            { return Results.BadRequest(new { code = "InvalidWebHostRegistration" }); }
            catch (Exception error) when (error is InvalidOperationException or DbUpdateException)
            { return Results.Conflict(new { code = "WebHostRegistrationUnavailable" }); }
        });
        owners.MapPost("/{hostId:guid}/revoke", async (Guid organizationId, Guid hostId, HttpContext http,
            WebHostRegistryService service, IAuditEventWriter audit, CancellationToken token) =>
        {
            if (http.User.GetApplicationUserId() is not { } userId) return Results.Forbid();
            try
            {
                await service.RevokeAsync(organizationId, hostId, userId, token);
                await audit.WriteAsync("web-host.revoked", "WebHost", hostId,
                    "The product host identity was revoked and preview access disabled.", cancellationToken: token);
                return Results.NoContent();
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Results.StatusCode(408); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { code = "WebHostChanged" }); }
        });

        // This endpoint uses an independent signed host identity, never cookies or agent/Office credentials.
        endpoints.MapPost("/api/web-host/v1/heartbeat", async (HttpContext http, WebHostRegistryService service, CancellationToken token) =>
        {
            if (!http.Request.IsHttps) return Results.BadRequest(new { code = "HttpsRequired" });
            try
            {
                var message = await ReadAsync<SignedWebHostMessage>(http.Request, 2 * 1024 * 1024, token);
                return Results.Ok(await service.HeartbeatAsync(message, token));
            }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Results.StatusCode(408); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { code = "WebHostMessageConsumed" }); }
            catch (Exception error) when (error is ArgumentException or JsonException or InvalidDataException or
                FormatException or System.Security.Cryptography.CryptographicException)
            { return Results.BadRequest(new { code = "InvalidWebHostMessage" }); }
        }).AllowAnonymous().RequireRateLimiting(CSweet.Api.Agents.AgentRateLimiting.WebHostHeartbeatPolicy);
        endpoints.MapWebHostDispatchEndpoints();
        return endpoints;
    }

    internal static async Task<T> ReadAsync<T>(HttpRequest request, int limit, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        token = deadline.Token;
        if (request.ContentLength > limit) throw new InvalidDataException("The host message is too large.");
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer, token);
            if (read == 0) break;
            if (body.Length + read > limit) throw new InvalidDataException("The host message is too large.");
            body.Write(buffer, 0, read);
        }
        return JsonSerializer.Deserialize<T>(body.ToArray(), PreviewJson.Options)
            ?? throw new InvalidDataException("A host message is required.");
    }
}
