using System.Text.Json;
using CSweet.Mobile.Transport;

namespace CSweet.Api.Auth;

public enum MobileAccess { Read, Write }

/// <summary>
/// Marks a route a phone may call through the tunnel (TS-04, first cut). Marking a write requires that its service
/// already deduplicates by the request's idempotency key and checks ownership of the target record; the marker is the
/// record of that review. Unmarked writes are 404 to phones.
/// </summary>
public sealed class AllowFromMobileAttribute(MobileAccess access, string reviewed) : Attribute
{
    public MobileAccess Access { get; } = access;
    /// <summary>Who checked idempotency and ownership, and where (kept short; the docs hold the detail).</summary>
    public string Reviewed { get; } = reviewed;
}

public static class MobileAccessExtensions
{
    public static TBuilder AllowFromMobile<TBuilder>(this TBuilder builder, MobileAccess access, string reviewed)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AllowFromMobileAttribute(access, reviewed));
}

/// <summary>
/// Runs after routing for requests that entered through the mobile dispatcher (never for ordinary HTTP). A write must
/// hit a route marked for mobile writes and carry an operation key equal to the body's <c>idempotencyKey</c>, so the
/// key the phone stores and retries with is the key the domain service deduplicates on.
/// </summary>
public sealed class MobileDispatchGateMiddleware(RequestDelegate next)
{
    public const string OperationKeyHeader = "X-CSweet-Operation-Key";

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Features.Get<MobileDispatchFeature>() is null || HttpMethods.IsGet(context.Request.Method))
        {
            await next(context);
            return;
        }
        var marker = context.GetEndpoint()?.Metadata.GetMetadata<AllowFromMobileAttribute>();
        if (marker?.Access != MobileAccess.Write)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var key = context.Request.Headers[OperationKeyHeader].ToString();
        context.Request.EnableBuffering();
        var bodyKey = await ReadIdempotencyKeyAsync(context.Request.Body, context.RequestAborted);
        context.Request.Body.Position = 0;
        if (!IsBound(key, bodyKey))
        {
            await Results.Problem(title: "This action needs an operation key that matches the request.",
                statusCode: StatusCodes.Status428PreconditionRequired,
                extensions: new Dictionary<string, object?> { ["errorCode"] = "operation_key_required" }).ExecuteAsync(context);
            return;
        }
        await next(context);
    }

    /// <summary>The operation key is present, within length, and is the same key the domain service receives.</summary>
    public static bool IsBound(string? operationKey, string? bodyIdempotencyKey) =>
        !string.IsNullOrWhiteSpace(operationKey) && operationKey.Length <= MobileRequest.MaxOperationKeyLength &&
        string.Equals(operationKey, bodyIdempotencyKey?.Trim(), StringComparison.Ordinal);

    public static async Task<string?> ReadIdempotencyKeyAsync(Stream body, CancellationToken ct)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "idempotencyKey", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
