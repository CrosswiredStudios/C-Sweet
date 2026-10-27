using CSweet.Api.Auth;
using CSweet.Contracts.Plugins;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.Mvc;
namespace CSweet.Api.Agents;
public static class RemoteMcpAccountEndpoints
{
    public static IEndpointRouteBuilder MapRemoteMcpAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/businesses/{business:guid}/plugins/{provider:guid}/api-key-accounts").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (http.User.GetApplicationUserId() is null) return Results.Forbid();
            http.Response.Headers.CacheControl = "no-store";
            if (http.Request.ContentLength > 4096) return Results.StatusCode(413);
            // Header must be supplied by the native same-origin client, never a cross-site HTML form.
            if (http.Request.Method != "GET" && http.Request.Headers["X-CSweet-Native-Account"] != "1") return Results.Forbid();
            try { return await next(context); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.Text.Json.JsonException or Microsoft.EntityFrameworkCore.DbUpdateException)
            { return Results.BadRequest(new { error = "The connection changed or the request is invalid. Refresh and review access again." }); }
        });
        group.MapGet("/", async (Guid business, Guid provider, HttpContext http, [FromServices] RemoteMcpAccountService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(http.User.GetApplicationUserId()!.Value, business, provider, ct)));
        group.MapPost("/", async (Guid business, Guid provider, HttpContext http, [FromServices] RemoteMcpAccountService service, CancellationToken ct) =>
        { var request = await ReadKey(http, ct); try { return Results.Ok(await service.ConnectAsync(http.User.GetApplicationUserId()!.Value, business, provider, request, ct)); } finally { request.Key = ""; } });
        group.MapPut("/{connection:guid}/key", async (Guid business, Guid provider, Guid connection, HttpContext http, [FromServices] RemoteMcpAccountService service, CancellationToken ct) =>
        { var request = await ReadKey(http, ct); try { await service.RotateAsync(http.User.GetApplicationUserId()!.Value, business, provider, connection, request, ct); return Results.NoContent(); } finally { request.Key = ""; } });
        group.MapPut("/{connection:guid}/grant", async (Guid business, Guid provider, Guid connection, RemoteMcpGrantRequest request, HttpContext http, [FromServices] RemoteMcpAccountService service, CancellationToken ct) =>
        { await service.GrantAsync(http.User.GetApplicationUserId()!.Value, business, provider, connection, request, ct); return Results.NoContent(); });
        group.MapPost("/{connection:guid}/revoke", async (Guid business, Guid provider, Guid connection, RemoteMcpRevokeRequest request, HttpContext http, [FromServices] RemoteMcpAccountService service, CancellationToken ct) =>
        { await service.RevokeAsync(http.User.GetApplicationUserId()!.Value, business, provider, connection, request.ExpectedGeneration, ct); return Results.NoContent(); });
        return endpoints;
    }
    private static async Task<RemoteMcpKeyRequest> ReadKey(HttpContext http, CancellationToken ct)
    {
        if (!http.Request.HasJsonContentType()) throw new ArgumentException("JSON input required.");
        var buffer = new byte[4097]; var total = 0;
        try
        {
            while (total < buffer.Length)
            {
                var count = await http.Request.Body.ReadAsync(buffer.AsMemory(total), ct);
                if (count == 0) break;
                total += count;
            }
            if (total > 4096) throw new ArgumentException("Input is too large.");
            using var document = System.Text.Json.JsonDocument.Parse(buffer.AsMemory(0, total));
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                document.RootElement.EnumerateObject().Select(p => p.Name).Distinct().Count() != document.RootElement.EnumerateObject().Count() ||
                document.RootElement.EnumerateObject().Any(p => p.Name is not ("key" or "expectedGeneration"))) throw new ArgumentException("Invalid key input.");
            return System.Text.Json.JsonSerializer.Deserialize<RemoteMcpKeyRequest>(document, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
                ?? throw new ArgumentException("Invalid key input.");
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer); }
    }

}
