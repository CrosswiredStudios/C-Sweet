using System.Text.Json;
using CSweet.Infrastructure.Setup;
using CSweet.WebHost.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Core;

public static class WebHostDispatchEndpoints
{
    public static void MapWebHostDispatchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        foreach (var action in new[] { "poll", "result", "artifact" })
        {
            var operation = action;
            endpoints.MapPost("/api/web-host/v1/" + operation, async (HttpContext http, WebPreviewExecutionService service, CancellationToken token) =>
            {
                if (!http.Request.IsHttps) return Results.BadRequest(new { code = "HttpsRequired" });
                try
                {
                    var message = await WebHostEndpoints.ReadAsync<SignedWebHostMessage>(http.Request,
                        operation == "result" ? 24 * 1024 * 1024 : 2 * 1024 * 1024, token);
                    if (operation == "poll") return Results.Ok(await service.PollAsync(message, token));
                    if (operation == "result") return Results.Ok(await service.CompleteAsync(message, token));
                    var artifact = await service.ArtifactAsync(message, token);
                    http.Response.Headers.CacheControl = "no-store";
                    return Results.Stream(artifact.Content, "application/octet-stream");
                }
                catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Results.StatusCode(408); }
                catch (Exception error) when (error is DbUpdateException or InvalidOperationException)
                { return Results.Conflict(new { code = "WebHostCommandChanged" }); }
                catch (Exception error) when (error is ArgumentException or JsonException or IOException or FormatException or System.Security.Cryptography.CryptographicException)
                { return Results.BadRequest(new { code = "InvalidWebHostCommand" }); }
            }).AllowAnonymous().RequireRateLimiting(CSweet.Api.Agents.AgentRateLimiting.WebHostDispatchPolicy);
        }
    }
}
