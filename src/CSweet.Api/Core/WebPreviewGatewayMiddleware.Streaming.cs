using System.Globalization;
using System.Net.Http.Headers;
using CSweet.Infrastructure.Setup;
using CSweet.WebHost.Core;

namespace CSweet.Api.Core;

public sealed partial class WebPreviewGatewayMiddleware
{
    private const long MaximumAssetBytes = 256L * 1024 * 1024;
    private static async Task ForwardAsync(HttpContext context, IWebPreviewGateway gateway, Guid preview,
        string cookie, Dictionary<string, string> headers, byte[] body)
    {
        var productCookies = context.Request.Cookies.Where(x => x.Key != WebPreviewOrigins.CookieName && ProductGuestProtocol.ValidCookie(x.Key, x.Value))
            .Take(32).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        var path = context.Request.Path + context.Request.QueryString;
        long desiredStart = 0, desiredEnd = MaximumAssetBytes - 1;
        var clientRange = headers.TryGetValue("Range", out var range);
        var chunked = context.Request.Method == "GET";
        if (chunked && clientRange)
        {
            var head = await gateway.SendAsync(preview, cookie, new("HEAD", path, new Dictionary<string, string>(), [], productCookies), context.RequestAborted);
            if (head.StatusCode != 200 || !head.Headers.TryGetValue("Content-Length", out var lengthText) ||
                !long.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length is < 1 or > MaximumAssetBytes ||
                !RangeHeaderValue.TryParse(range, out var parsed) || parsed.Unit != "bytes" || parsed.Ranges.Count != 1)
            { context.Response.StatusCode = 416; return; }
            var part = parsed.Ranges.Single();
            desiredStart = part.From ?? Math.Max(0, length - (part.To ?? 0));
            desiredEnd = part.From is null ? length - 1 : Math.Min(length - 1, part.To ?? length - 1);
            if (desiredStart > desiredEnd || desiredStart >= length)
            { context.Response.StatusCode = 416; context.Response.Headers.ContentRange = "bytes */" + length; return; }
        }
        if (chunked) headers["Range"] = $"bytes={desiredStart}-{Math.Min(desiredEnd, desiredStart + ProductGuestProtocol.MaximumHttpBodyBytes - 1)}";
        var request = new GuestHttpRequest(context.Request.Method, path, headers, body, productCookies);
        var response = await gateway.SendAsync(preview, cookie, request, context.RequestAborted);
        if (!clientRange && context.Request.Method == "GET") response = InjectDiagnostics(response);
        context.Response.StatusCode = response.StatusCode;
        CopyHeaders(context, response);
        if (chunked && response.StatusCode == 206)
        {
            var first = ReadRange(response, desiredStart);
            var end = Math.Min(desiredEnd, first.Length!.Value - 1);
            if (first.To != Math.Min(end, desiredStart + ProductGuestProtocol.MaximumHttpBodyBytes - 1))
                throw new InvalidDataException("The preview returned an incomplete range.");
            context.Response.StatusCode = clientRange ? 206 : 200;
            context.Response.ContentLength = end - desiredStart + 1;
            if (clientRange) context.Response.Headers.ContentRange = $"bytes {desiredStart}-{end}/{first.Length}";
            else context.Response.Headers.Remove("Content-Range");
            var etag = response.Headers.GetValueOrDefault("ETag");
            if (first.To < end && (etag is null || etag.StartsWith("W/", StringComparison.Ordinal)))
                throw new InvalidDataException("Streaming requires an immutable representation.");
            await context.Response.Body.WriteAsync(response.Body, context.RequestAborted);
            for (var offset = first.To!.Value + 1; offset <= end;)
            {
                headers["Range"] = $"bytes={offset}-{Math.Min(end, offset + ProductGuestProtocol.MaximumHttpBodyBytes - 1)}";
                // Every chunk obtains a fresh live session/grant check at enqueue, dispatch and response.
                var next = await gateway.SendAsync(preview, cookie, request, context.RequestAborted);
                var nextRange = ReadRange(next, offset);
                if (nextRange.Length != first.Length || nextRange.To != Math.Min(end, offset + ProductGuestProtocol.MaximumHttpBodyBytes - 1) ||
                    next.Headers.GetValueOrDefault("ETag") != etag ||
                    next.Headers.GetValueOrDefault("Content-Encoding") != response.Headers.GetValueOrDefault("Content-Encoding"))
                    throw new InvalidDataException("The preview representation changed during streaming.");
                await context.Response.Body.WriteAsync(next.Body, context.RequestAborted);
                offset = nextRange.To!.Value + 1;
            }
            return;
        }
        if (context.Request.Method == "HEAD")
        {
            if (response.StatusCode == 200 && response.Headers.TryGetValue("Content-Length", out var text) &&
                long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var length) && length is >= 0 and <= MaximumAssetBytes)
                context.Response.ContentLength = length;
            return;
        }
        if (response.StatusCode is 204 or 304) return;
        context.Response.ContentLength = response.Body.Length;
        await context.Response.Body.WriteAsync(response.Body, context.RequestAborted);
    }
    private static ContentRangeHeaderValue ReadRange(GuestHttpResponse response, long expectedStart)
    {
        if (response.StatusCode != 206 || !response.Headers.TryGetValue("Content-Range", out var text) ||
            !ContentRangeHeaderValue.TryParse(text, out var range) || range.Unit != "bytes" || !range.HasRange || !range.HasLength ||
            range.Length is < 1 or > MaximumAssetBytes || range.From != expectedStart || range.To >= range.Length ||
            range.To - range.From + 1 != response.Body.Length || response.Body.Length == 0)
            throw new InvalidDataException("The preview returned an invalid range.");
        return range;
    }
    private static void CopyHeaders(HttpContext context, GuestHttpResponse response)
    {
        if (response.SetCookies is { } cookies)
        {
            if (cookies.Count > 32 || cookies.Any(x => x is null) || cookies.Sum(x => (long)x.Length) > 32768)
                throw new InvalidDataException("Product cookies exceed their bound.");
            foreach (var raw in cookies)
            {
                if (!Microsoft.Net.Http.Headers.SetCookieHeaderValue.TryParse(raw, out var cookie) || cookie.Name.Value == WebPreviewOrigins.CookieName ||
                    !ProductGuestProtocol.ValidCookie(cookie.Name.Value!, cookie.Value.Value) || raw.Any(char.IsControl)) continue;
                cookie.Domain = default; cookie.Path = "/"; cookie.Secure = true;
                cookie.SameSite = Microsoft.Net.Http.Headers.SameSiteMode.Strict;
                if (cookie.MaxAge > TimeSpan.FromMinutes(30)) cookie.MaxAge = TimeSpan.FromMinutes(30);
                if (cookie.Expires > DateTimeOffset.UtcNow.AddMinutes(30)) cookie.Expires = DateTimeOffset.UtcNow.AddMinutes(30);
                context.Response.Headers.Append("Set-Cookie", cookie.ToString());
            }
        }
        foreach (var header in response.Headers)
        {
            if (!ResponseHeaders.Contains(header.Key)) continue;
            if (header.Value is null || header.Value.Length > 8192 || header.Value.Any(char.IsControl))
                throw new InvalidDataException("Invalid response header.");
            context.Response.Headers[header.Key] = header.Value;
        }
    }
    private static GuestHttpResponse InjectDiagnostics(GuestHttpResponse response)
    {
        if (response.Body.Length > ProductGuestProtocol.MaximumHttpBodyBytes - 256 || response.Headers.ContainsKey("Content-Encoding") ||
            !response.Headers.TryGetValue("Content-Type", out var type) || !type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("charset=", StringComparison.OrdinalIgnoreCase) && !type.Contains("charset=utf-8", StringComparison.OrdinalIgnoreCase)) return response;
        if (response.StatusCode == 206)
        {
            var range = ReadRange(response, 0);
            if (range.To != range.Length - 1) return response;
        }
        else if (response.StatusCode != 200) return response;
        var text = System.Text.Encoding.UTF8.GetString(response.Body);
        var head = System.Text.RegularExpressions.Regex.Match(text, @"<head(?:\s[^>]*)?>", System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!head.Success) return response;
        text = text.Insert(head.Index + head.Length, "<script src=\"/__preview/client.js\"></script>");
        var headers = response.Headers.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        headers.Remove("Content-Range"); headers.Remove("Content-Length"); headers.Remove("ETag");
        return response with { StatusCode = 200, Headers = headers, Body = System.Text.Encoding.UTF8.GetBytes(text) };
    }}
