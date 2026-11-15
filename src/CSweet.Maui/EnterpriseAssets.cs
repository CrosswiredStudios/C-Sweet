using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Mobile.Transport;

namespace CSweet.Maui;

/// <summary>
/// The web app files and API reads for one enterprise, over its Headquarters session (TS-13 prototype). Files come
/// only from Headquarters' published manifest, are checked against the manifest's SHA-256 and kept in a cache keyed
/// by that hash. Nothing is fetched from the network directly.
/// </summary>
public sealed class EnterpriseAssets(PhoneTunnelClient client, string cacheDirectory)
{
    private const long MaxFileBytes = 64L * 1024 * 1024;
    private const long MaxApiBytes = 4L * 1024 * 1024;
    private Task<IReadOnlyDictionary<string, Entry>>? _manifest;
    private readonly Lock _gate = new();

    public sealed record Entry(string Path, string Hash, long Size, string ContentType);

    /// <summary>A response for the WebView: status, content type and body (already decompressed).</summary>
    public sealed record Served(int Status, string? ContentType, Stream Body)
    {
        public static Served Empty(int status) => new(status, null, Stream.Null);
    }

    public Task<IReadOnlyDictionary<string, Entry>> ManifestAsync()
    {
        lock (_gate) return _manifest ??= LoadManifestAsync();
    }

    private async Task<IReadOnlyDictionary<string, Entry>> LoadManifestAsync()
    {
        var reply = await client.SendAsync("GET", "/.csweet/manifest", 4 * 1024 * 1024, CancellationToken.None);
        if (reply.Status != 200) throw new InvalidDataException($"Headquarters has no published web app ({reply.Status}).");
        var entries = JsonSerializer.Deserialize<List<Entry>>(reply.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        return entries.Where(x => x.Path.StartsWith('/') && x.Hash.Length == 64).ToDictionary(x => x.Path, StringComparer.Ordinal);
    }

    /// <summary>A GET from the WebView for this enterprise's origin.</summary>
    public async Task<Served> GetAsync(string path, string? query, CancellationToken ct)
    {
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            var api = await ApiAsync("GET", path + query, ct);
            return new Served(api.Status, api.ContentType, new MemoryStream(api.Body));
        }
        var manifest = await ManifestAsync();
        if (!manifest.TryGetValue(path, out var entry))
        {
            // Client-side routes (/agents, /settings/phones) load the app's start page.
            var last = path[(path.LastIndexOf('/') + 1)..];
            if (last.Contains('.') || !manifest.TryGetValue("/index.html", out entry)) return Served.Empty(404);
        }
        var cached = Path.Combine(cacheDirectory, entry.Hash);
        if (!File.Exists(cached))
        {
            var reply = await client.SendAsync("GET", entry.Path, MaxFileBytes, ct);
            if (reply.Status != 200) return Served.Empty(reply.Status);
            var bytes = Decode(reply);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), entry.Hash, StringComparison.Ordinal))
                return Served.Empty(502); // not the file the manifest promised
            Directory.CreateDirectory(cacheDirectory);
            var temporary = cached + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, ct);
            File.Move(temporary, cached, overwrite: true);
        }
        return new Served(200, entry.ContentType, File.OpenRead(cached));
    }

    public Task<TunnelReply> ApiAsync(string method, string pathAndQuery, CancellationToken ct) =>
        ApiAsync(method, pathAndQuery, null, null, null, ct);

    /// <summary>
    /// An API call from the web app. A write needs a JSON body within 256 KiB and an operation key (the body's
    /// idempotency key); Headquarters only accepts it on routes marked for phone writes. After a lost response the
    /// page's retry sends the same key, and the domain service returns the first result instead of acting twice.
    /// </summary>
    public async Task<TunnelReply> ApiAsync(string method, string pathAndQuery, string? contentType, string? body, string? operationKey, CancellationToken ct)
    {
        if (!pathAndQuery.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return TunnelReply.Empty(404);
        method = method.ToUpperInvariant();
        MobileRequest request;
        if (method == "GET") request = new MobileRequest("", 0, "GET", pathAndQuery);
        else if (method is "POST" or "PUT" or "PATCH" or "DELETE")
        {
            if (string.IsNullOrWhiteSpace(operationKey) || operationKey.Length > MobileRequest.MaxOperationKeyLength) return TunnelReply.Empty(428);
            if (body is { } encoded && encoded.Length > (MobileRequest.MaxBodyBytes + 2) / 3 * 4) return TunnelReply.Empty(413);
            request = new MobileRequest("", 0, method, pathAndQuery, contentType ?? "application/json", body ?? "", operationKey);
        }
        else return TunnelReply.Empty(405);
        var reply = await client.SendAsync(request, MaxApiBytes, ct);
        return reply with { ContentEncoding = null, Body = Decode(reply) };
    }

    private static byte[] Decode(TunnelReply reply)
    {
        if (reply.ContentEncoding != "br") return reply.Body;
        using var input = new BrotliStream(new MemoryStream(reply.Body), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
