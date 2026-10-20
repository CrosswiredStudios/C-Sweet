using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Infrastructure.Compute;
using Microsoft.AspNetCore.Mvc;

#if COMPUTE_GATEWAY
namespace CSweet.ExecutionGateway.Compute;
#else
namespace CSweet.Api.Compute;
#endif

public static class ComputeWorkEndpoints
{
    internal static Task<IResult> ConfirmRetirementAsync(HttpContext http,
        [FromServices] ComputeProviderRetirementService service, CancellationToken token) => RetirementAsync(http, service, true, token);
    internal static Task<IResult> AuthorizeRetirementAsync(HttpContext http,
        [FromServices] ComputeProviderRetirementService service, CancellationToken token) => RetirementAsync(http, service, false, token);
    private static async Task<IResult> RetirementAsync(HttpContext http,
        ComputeProviderRetirementService service, bool confirm, CancellationToken token)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!http.Request.IsHttps) return Results.StatusCode(403);
        if (!http.Request.HasJsonContentType() || http.Request.ContentLength > 4096 ||
            !string.IsNullOrEmpty(http.Request.Headers.ContentEncoding)) return Results.BadRequest();
        try
        {
            var bytes = new byte[4097];
            var length = 0;
            while (length < bytes.Length)
            {
                var count = await http.Request.Body.ReadAsync(bytes.AsMemory(length), token);
                if (count == 0) break;
                length += count;
            }
            if (length > 4096) return Results.StatusCode(413);
            var receipt = JsonSerializer.Deserialize<ComputeProviderRetirementReceipt>(bytes.AsSpan(0, length), ComputeProtocol.Json);
            if (receipt is null) return Results.BadRequest();
            if (confirm) await service.ConfirmAsync(receipt, token);
            else if (!await service.AuthorizeAsync(receipt, token)) return Results.Conflict();
            return Results.NoContent();
        }
        catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
        catch (JsonException) { return Results.BadRequest(); }
        catch (InvalidOperationException) { return Results.Conflict(); }
    }
    internal static Task<IResult> DependencyAccessAsync(HttpContext http, [FromServices] ComputeDependencyAccess access, CancellationToken token) =>
        ReceiveAsync(http, async envelope => Results.Json(await access.AuthorizeAsync(envelope, token), ComputeProtocol.Json), token);
    internal static Task<IResult> PublicationAccessAsync(HttpContext http, [FromServices] ComputePublicationAccess access, CancellationToken token) =>
        ReceiveAsync(http, async envelope => Results.Json(await access.AuthorizeAsync(envelope, token), ComputeProtocol.Json), token);
    internal static Task<IResult> WakeAsync(HttpContext http, [FromServices] ComputeProviderWorkService service,
        [FromServices] ComputeProviderWakeChannels channels, CancellationToken token) => ReceiveAsync(http, async envelope =>
    {
        var node = await service.AuthorizeWakeAsync(envelope, token);
        using var subscription = channels.Subscribe(node.OrganizationId, node.NodeId, node.ProviderId);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        Guid eventId;
        try { eventId = await subscription.ReadAsync(linked.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Results.NoContent(); }
        // Recheck enrollment and proof expiry before any event leaves the server.
        await service.AuthorizeWakeAsync(envelope, token);
        return Results.Json(new ComputeProviderWakeHint(eventId), ComputeProtocol.Json);
    }, token);
    internal static Task<IResult> DiscoverAsync(HttpContext http, [FromServices] ComputeProviderWorkService service, CancellationToken token) =>
        ReceiveAsync(http, async envelope => Results.Json(await service.DiscoverAsync(envelope, token), ComputeProtocol.Json), token);

    internal static Task<IResult> ClaimAsync(HttpContext http, [FromServices] ComputeProviderWorkService service, CancellationToken token) =>
        ReceiveAsync(http, async envelope =>
        {
            var packet = await service.ClaimAsync(envelope, token);
            return packet is null ? Results.NoContent() : Results.Json(packet, ComputeProtocol.Json);
        }, token);

    internal static Task<IResult> ReceiptAsync(HttpContext http, [FromServices] ComputeProviderWorkService service, CancellationToken token) =>
        ReceiveAsync(http, async envelope =>
        {
            var receipt = await service.ReadResultReceiptAsync(envelope, token);
            return receipt is null ? Results.NoContent() : Results.Json(receipt, ComputeProtocol.Json);
        }, token);

    private static async Task<IResult> ReceiveAsync(HttpContext http, Func<SignedComputeProviderWorkRequest, Task<IResult>> receive, CancellationToken token)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!http.Request.IsHttps) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!http.Request.HasJsonContentType() || !string.IsNullOrEmpty(http.Request.Headers.ContentEncoding))
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        if (http.Request.ContentLength > ComputeWorkTransport.MaximumRequestBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        try
        {
            using var body = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = await http.Request.Body.ReadAsync(buffer, token)) > 0)
            {
                if (body.Length + count > ComputeWorkTransport.MaximumRequestBytes)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                body.Write(buffer, 0, count);
            }
            var envelope = JsonSerializer.Deserialize<SignedComputeProviderWorkRequest>(body.GetBuffer().AsSpan(0, (int)body.Length), ComputeProtocol.Json);
            return envelope is null ? Results.BadRequest() : await receive(envelope);
        }
        catch (JsonException) { return Results.BadRequest(); }
        catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
        { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
        catch (Exception error) when (error is IOException or InvalidOperationException or Microsoft.EntityFrameworkCore.DbUpdateException)
        { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }
}
