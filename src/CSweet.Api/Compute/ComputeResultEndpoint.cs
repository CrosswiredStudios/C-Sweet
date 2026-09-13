using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Infrastructure.Compute;
using Microsoft.AspNetCore.Mvc;

#if COMPUTE_GATEWAY
namespace CSweet.ExecutionGateway.Compute;
#else
namespace CSweet.Api.Compute;
#endif

public static class ComputeResultEndpoint
{
    internal static async Task<IResult> ReceiveAsync(HttpContext http, [FromServices] ComputeResultReconciler reconciler, CancellationToken token)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!http.Request.IsHttps) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!http.Request.HasJsonContentType() || !string.IsNullOrEmpty(http.Request.Headers.ContentEncoding))
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        if (http.Request.ContentLength > ComputeResultTransport.MaximumRequestBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        try
        {
            using var body = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = await http.Request.Body.ReadAsync(buffer, token)) > 0)
            {
                if (body.Length + count > ComputeResultTransport.MaximumRequestBytes)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                body.Write(buffer, 0, count);
            }
            var envelope = JsonSerializer.Deserialize<SignedComputeProviderResult>(body.GetBuffer().AsSpan(0, (int)body.Length), ComputeProtocol.Json);
            if (envelope is null) return Results.BadRequest();
            var applied = await reconciler.ApplyAsync(envelope, token);
            var result = JsonSerializer.Deserialize<ComputeProviderResult>(envelope.PayloadJson, ComputeProtocol.Json)!;
            return Results.Json(new ComputeResultAcknowledgement(result.OperationId, result.Sequence,
                ComputeProtocol.Digest(envelope.PayloadJson), applied), ComputeProtocol.Json);
        }
        catch (JsonException) { return Results.BadRequest(); }
        catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
        { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
        catch (Exception error) when (error is IOException or InvalidOperationException or Microsoft.EntityFrameworkCore.DbUpdateException)
        { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }
}
