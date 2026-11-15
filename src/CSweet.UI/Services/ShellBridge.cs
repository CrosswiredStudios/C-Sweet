using System.Net;
using Microsoft.JSInterop;

namespace CSweet.UI.Services;

/// <summary>
/// Thin-shell mode (TS-06/TS-13 prototype): the web app runs inside the C-Sweet phone app at a per-enterprise origin
/// that never resolves on the network. API calls do not use fetch; they go to the native shell through the bridge
/// script (<c>shell-bridge.js</c>), which sends them through the encrypted tunnel to that enterprise's Headquarters.
/// No cookies or antiforgery tokens exist on this path; Headquarters knows the person from the phone's session.
/// </summary>
public static class ShellBridge
{
    public const string OriginSuffix = ".csweet.invalid";

    public static bool IsActive { get; set; }

    public static bool IsShellOrigin(Uri baseAddress) =>
        baseAddress.Scheme == Uri.UriSchemeHttps && baseAddress.Host.EndsWith(OriginSuffix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Sends each API request to the native shell instead of the network.</summary>
public sealed class ShellBridgeHttpHandler(IJSRuntime js) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var bytes = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = request.Content?.Headers.ContentType?.ToString();
        // A write's operation key is the idempotency key the page already puts in the body, so a retry after a lost
        // response is deduplicated by the same service that handles desktop retries.
        var operationKey = request.Method == HttpMethod.Get ? null
            : request.Headers.TryGetValues("X-CSweet-Operation-Key", out var keys) ? keys.FirstOrDefault()
            : IdempotencyKey(bytes);
        var reply = await js.InvokeAsync<BridgeReply>("csweetShellBridge.request", cancellationToken,
            request.Method.Method, request.RequestUri!.PathAndQuery, contentType, bytes is null ? null : Convert.ToBase64String(bytes), operationKey);
        var response = new HttpResponseMessage((HttpStatusCode)reply.Status)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(string.IsNullOrEmpty(reply.Body) ? [] : Convert.FromBase64String(reply.Body))
        };
        if (!string.IsNullOrEmpty(reply.ContentType)) response.Content.Headers.TryAddWithoutValidation("Content-Type", reply.ContentType);
        return response;
    }

    internal static string? IdempotencyKey(byte[]? json)
    {
        if (json is not { Length: > 0 }) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "idempotencyKey", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    return property.Value.GetString()?.Trim();
            }
        }
        catch (System.Text.Json.JsonException) { }
        return null;
    }

    private sealed record BridgeReply(int Status, string? ContentType, string? Body);
}
