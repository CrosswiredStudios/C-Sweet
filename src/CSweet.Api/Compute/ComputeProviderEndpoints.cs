using System.Text.Json;
using System.Threading.RateLimiting;
using CSweet.Compute.Contracts;
using CSweet.Infrastructure.Compute;
using Microsoft.AspNetCore.RateLimiting;

#if COMPUTE_GATEWAY
namespace CSweet.ExecutionGateway.Compute;
#else
namespace CSweet.Api.Compute;
#endif

public static class ComputeProviderEndpoints
{
    public const string MaintenanceRatePolicy = "compute-provider-maintenance";

    public static IServiceCollection AddComputeProviderIngress(this IServiceCollection services)
    {
        services.AddSingleton<ComputeProviderWakeChannels>();
        services.AddSingleton<CSweet.Application.Compute.IComputeProviderWakePublisher>(sp => sp.GetRequiredService<ComputeProviderWakeChannels>());
        services.AddScoped<ComputeProviderWakeDispatcher>();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(MaintenanceRatePolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
        });
        return services;
    }

    public static IEndpointRouteBuilder MapComputeProviderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(ComputeMaintenanceTransport.Path, ReceiveAsync)
            // Signature/enrollment verification is the sole authority; cookies or agent credentials do not authorize evidence.
            .AllowAnonymous().RequireRateLimiting(MaintenanceRatePolicy)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(ComputeMaintenanceTransport.MaximumRequestBytes));
        endpoints.MapPost(ComputeResultTransport.Path, ComputeResultEndpoint.ReceiveAsync)
            .AllowAnonymous().RequireRateLimiting(MaintenanceRatePolicy)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(ComputeResultTransport.MaximumRequestBytes));
        endpoints.MapPost(ComputeWorkTransport.DiscoveryPath, ComputeWorkEndpoints.DiscoverAsync)
            .AllowAnonymous().RequireRateLimiting(MaintenanceRatePolicy)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(ComputeWorkTransport.MaximumRequestBytes));
        endpoints.MapPost(ComputeWorkTransport.ClaimPath, ComputeWorkEndpoints.ClaimAsync)
            .AllowAnonymous().RequireRateLimiting(MaintenanceRatePolicy)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(ComputeWorkTransport.MaximumRequestBytes));
        endpoints.MapPost(ComputeWorkTransport.ReceiptPath, ComputeWorkEndpoints.ReceiptAsync)
            .AllowAnonymous().RequireRateLimiting(MaintenanceRatePolicy)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(ComputeWorkTransport.MaximumRequestBytes));
        endpoints.MapPost(ComputeProviderWakeTransport.Path, ComputeWorkEndpoints.WakeAsync)
            .AllowAnonymous().RequireRateLimiting(MaintenanceRatePolicy)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(ComputeWorkTransport.MaximumRequestBytes));
        endpoints.MapPost(ComputeWorkTransport.PublicationAccessPath, ComputeWorkEndpoints.PublicationAccessAsync)
            .AllowAnonymous().RequireRateLimiting(MaintenanceRatePolicy)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(ComputeWorkTransport.MaximumRequestBytes));
        return endpoints;
    }

    internal static async Task<IResult> ReceiveAsync(HttpContext http, ComputeMaintenanceIngestor ingestor, CancellationToken token)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!http.Request.IsHttps) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!http.Request.HasJsonContentType() || !string.IsNullOrEmpty(http.Request.Headers.ContentEncoding))
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        if (http.Request.ContentLength > ComputeMaintenanceTransport.MaximumRequestBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        try
        {
            using var body = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = await http.Request.Body.ReadAsync(buffer, token)) > 0)
            {
                if (body.Length + count > ComputeMaintenanceTransport.MaximumRequestBytes)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                body.Write(buffer, 0, count);
            }
            var envelope = JsonSerializer.Deserialize<SignedComputeMaintenanceDelivery>(body.GetBuffer().AsSpan(0, (int)body.Length), ComputeProtocol.Json);
            if (envelope is null) return Results.BadRequest();
            var eventId = await ingestor.ApplyAsync(envelope, token);
            // The ingestor verified these bytes and completed its durable ledger append before returning.
            var delivery = JsonSerializer.Deserialize<ComputeMaintenanceDelivery>(envelope.PayloadJson, ComputeProtocol.Json)!;
            return Results.Json(new ComputeMaintenanceAcknowledgement(eventId, delivery.EventDigest), ComputeProtocol.Json);
        }
        catch (JsonException) { return Results.BadRequest(); }
        catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
        { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
        // Conflicting immutable IDs and unavailable ledger storage must never acknowledge delivery.
        catch (Exception error) when (error is IOException or InvalidOperationException)
        { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }
}
