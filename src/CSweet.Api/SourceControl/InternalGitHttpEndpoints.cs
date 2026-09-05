using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CSweet.Contracts.SourceControl;
using CSweet.Infrastructure.SourceControl;
using CSweet.TrustedServices;

namespace CSweet.Api.SourceControl;

public static partial class InternalGitHttpEndpoints
{
    private static async Task<IResult> LfsAsync(Guid business, Guid repository, string path, string token, HttpContext http, InternalGitAccessService service, CancellationToken ct)
    {
        if (path == "info/lfs/locks" || path.StartsWith("info/lfs/locks/", StringComparison.Ordinal))
            return await LocksAsync(business, repository, path, token, http, service, ct);
        const string media = "application/vnd.git-lfs+json";
        if (path == "info/lfs/objects/batch" && HttpMethods.IsPost(http.Request.Method))
        {
            await service.AuthorizeAsync(business, repository, token, "git-upload-pack", ct);
            var bytes = await ReadBodyAsync(http.Request.Body, 1024 * 1024, ct);
            InternalGitLfsBatch? batch;
            try { batch = JsonSerializer.Deserialize<InternalGitLfsBatch>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
            catch (JsonException) { return Results.BadRequest(); }
            if (batch?.Objects is null || batch.Objects.Count > 100 || batch.Operation is not ("upload" or "download") ||
                (batch.Transfers is not null && !batch.Transfers.Contains("basic")) || batch.HashAlgo is not (null or "sha256")) return Results.BadRequest();
            await service.AuthorizeAsync(business, repository, token, batch.Operation == "upload" ? "git-receive-pack" : "git-upload-pack", ct);
            var objects = new List<object>();
            foreach (var item in batch.Objects)
            {
                if (item is null || !ValidOid(item.Oid) || item.Size < 0) return Results.BadRequest();
                if (item.Size > InternalGitRepositoryStore.MaximumGitRequestBytes)
                { objects.Add(new { oid = item.Oid, size = item.Size, error = new { code = 413, message = "Client transfers currently support objects up to 128 MiB." } }); continue; }
                var href = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}/git/{business:D}/{repository:D}.git/info/lfs/objects/{item.Oid}?size={item.Size}";
                objects.Add(new { oid = item.Oid, size = item.Size, authenticated = true,
                    actions = new Dictionary<string, object> { [batch.Operation] = new { href } } });
            }
            return Results.Json(new { transfer = "basic", objects }, contentType: media);
        }
        if (path.StartsWith("info/lfs/objects/", StringComparison.Ordinal))
        {
            var oid = path[17..]; if (!ValidOid(oid)) return Results.NotFound();
            var upload = HttpMethods.IsPut(http.Request.Method);
            if (!upload && !HttpMethods.IsGet(http.Request.Method)) return Results.StatusCode(405);
            await service.AuthorizeAsync(business, repository, token, upload ? "git-receive-pack" : "git-upload-pack", ct);
            if (!long.TryParse(http.Request.Query["size"], out var size) || size < 0 || size > InternalGitRepositoryStore.MaximumGitRequestBytes) return Results.BadRequest();
            var body = upload ? await ReadBodyAsync(http.Request.Body, InternalGitRepositoryStore.MaximumGitRequestBytes, ct) : [];
            var result = await service.TransferLfsAsync(business, repository, token, upload ? "upload" : "download", oid, size, body, ct);
            return upload ? Results.Ok() : Results.Bytes(result.Body, "application/octet-stream");
        }
        return Results.NotFound();
    }
    private static bool ValidOid(string? oid) => oid is not null && Regex.IsMatch(oid, "\\A[0-9a-f]{64}\\z");
    private static async Task<byte[]> ReadBodyAsync(Stream input, int limit, CancellationToken ct)
    {
        using var output = new MemoryStream(); var buffer = new byte[65536]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        { if (output.Length + count > limit) throw new BadHttpRequestException("Transfer too large.", 413); output.Write(buffer, 0, count); }
        return output.ToArray();
    }

    public static IEndpointRouteBuilder MapInternalGitHttpEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/git/{business:guid}/{repository:guid}.git/{**path}", ["GET", "POST", "PUT"], async (
            Guid business, Guid repository, string path, HttpContext http, InternalGitAccessService service, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!http.Request.IsHttps && !(http.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address)))
                return Results.StatusCode(403);
            try
            {
                if (!AuthenticationHeaderValue.TryParse(http.Request.Headers.Authorization, out var auth) || auth.Scheme != "Basic" || auth.Parameter is null || auth.Parameter.Length > 512)
                    throw new UnauthorizedAccessException();
                string decoded;
                try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter)); }
                catch (FormatException) { throw new UnauthorizedAccessException(); }
                if (!decoded.StartsWith("csweet:", StringComparison.Ordinal)) throw new UnauthorizedAccessException();
                if (path.StartsWith("info/lfs/", StringComparison.Ordinal))
                    return await LfsAsync(business, repository, path, decoded[7..], http, service, ct);
                var advertise = HttpMethods.IsGet(http.Request.Method) && path == "info/refs";
                var gitService = advertise ? http.Request.Query["service"].ToString() : path;
                if ((!advertise && !HttpMethods.IsPost(http.Request.Method)) || gitService is not ("git-upload-pack" or "git-receive-pack")) return Results.NotFound();
                if (!advertise && http.Request.ContentType != $"application/x-{gitService}-request") return Results.StatusCode(415);
                await service.AuthorizeAsync(business, repository, decoded[7..], gitService, ct);
                using var content = new MemoryStream();
                if (!advertise)
                {
                    if (http.Request.ContentLength > InternalGitRepositoryStore.MaximumGitRequestBytes) return Results.StatusCode(413);
                    var encoding = http.Request.Headers.ContentEncoding.ToString();
                    if (encoding is not ("" or "gzip")) return Results.StatusCode(415);
                    using var gzip = encoding == "gzip" ? new GZipStream(http.Request.Body, CompressionMode.Decompress, leaveOpen: true) : null;
                    var input = (Stream?)gzip ?? http.Request.Body;
                    var buffer = new byte[65536]; int count;
                    while ((count = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        if (content.Length + count > InternalGitRepositoryStore.MaximumGitRequestBytes) return Results.StatusCode(413);
                        content.Write(buffer, 0, count);
                    }
                }
                var result = await service.ExchangeAsync(business, repository, decoded[7..], gitService, advertise, content.ToArray(), ct);
                return Results.Bytes(result.Body, result.ContentType);
            }
            catch (UnauthorizedAccessException)
            {
                http.Response.Headers.WWWAuthenticate = "Basic realm=\"C-Sweet Git\", charset=\"UTF-8\"";
                return Results.StatusCode(401);
            }
            catch (ArgumentException) { return Results.BadRequest(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException)
            { return Results.StatusCode(503); }
        }).AllowAnonymous().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(InternalGitRepositoryStore.MaximumGitRequestBytes));
        return endpoints;
    }
}
