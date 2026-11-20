using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Mobile.Transport;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;

namespace CSweet.Api.Auth;

/// <summary>Thin-shell prototype (TS-13) settings. Off unless a web root is configured.</summary>
public sealed class MobileShellOptions
{
    public const string Section = "MobileShell";
    /// <summary>The published CSweet.App <c>wwwroot</c> folder (dotnet publish src/CSweet.App -o …, then …/wwwroot).</summary>
    public string? WebRoot { get; set; }
    /// <summary>
    /// Prototype only: let approved phones run every GET under /api, not just the allowlist. Reads still pass the real
    /// authorization; side-effecting reads are a known risk until routes are reviewed and marked (TS-04).
    /// </summary>
    public bool AllowAllReads { get; set; }
}

/// <summary>
/// The published web app files phones may load (TS-14 prototype). The manifest is built once from the web root: path,
/// SHA-256 and size. Only files in it are served, GET only, never by proxying the web frontend. A precompressed
/// <c>.br</c> sibling is sent instead when present.
/// </summary>
public sealed class MobileWebAssets(IOptions<MobileShellOptions> options, ILogger<MobileWebAssets> logger)
{
    public const string ManifestPath = "/.csweet/manifest";
    private static readonly FileExtensionContentTypeProvider Types = CreateTypes();
    private readonly Lazy<IReadOnlyDictionary<string, Asset>> _assets = new(() => Build(options.Value.WebRoot, logger));

    public sealed record Asset(string Path, string FullPath, string Hash, long Size, string ContentType, string? BrotliPath);

    public bool Enabled => _assets.Value.Count > 0;

    public TunnelReply Manifest() => new(200, "application/json", null, JsonSerializer.SerializeToUtf8Bytes(
        _assets.Value.Values.OrderBy(x => x.Path, StringComparer.Ordinal).Select(x => new { path = x.Path, hash = x.Hash, size = x.Size, contentType = x.ContentType })));

    public async Task<TunnelReply> ReadAsync(string path, CancellationToken ct)
    {
        if (!_assets.Value.TryGetValue(path, out var asset)) return TunnelReply.Empty(404);
        return asset.BrotliPath is { } brotli
            ? new TunnelReply(200, asset.ContentType, "br", await File.ReadAllBytesAsync(brotli, ct))
            : new TunnelReply(200, asset.ContentType, null, await File.ReadAllBytesAsync(asset.FullPath, ct));
    }

    private static IReadOnlyDictionary<string, Asset> Build(string? webRoot, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(webRoot) || !Directory.Exists(webRoot)) return new Dictionary<string, Asset>();
        var root = Path.GetFullPath(webRoot);
        // The URLs a published app requests are not its file names: fingerprinted files are asked for by plain name
        // (and the other way round), as the publish step's endpoints manifest records. Use it when it is there.
        var endpoints = Directory.GetParent(root) is { } parent
            ? Directory.EnumerateFiles(parent.FullName, "*.staticwebassets.endpoints.json").FirstOrDefault()
            : null;
        var assets = endpoints is null ? FromFiles(root) : FromEndpoints(root, endpoints);
        logger.LogInformation("Mobile shell: {Count} published web paths from {Root} ({Source}).", assets.Count, root,
            endpoints is null ? "files" : Path.GetFileName(endpoints));
        return assets;
    }

    private static Dictionary<string, Asset> FromFiles(string root)
    {
        var assets = new Dictionary<string, Asset>(StringComparer.Ordinal);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".br", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) continue;
            var relative = "/" + Path.GetRelativePath(root, file).Replace('\\', '/');
            var type = Types.TryGetContentType(file, out var known) ? known : "application/octet-stream";
            assets[relative] = Describe(relative, file, type, File.Exists(file + ".br") ? file + ".br" : null, hashes);
        }
        return assets;
    }

    private static Dictionary<string, Asset> FromEndpoints(string root, string manifestPath)
    {
        using var stream = File.OpenRead(manifestPath);
        var manifest = JsonSerializer.Deserialize<EndpointManifest>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Empty endpoints manifest.");
        string? Header(Endpoint endpoint, string name) =>
            endpoint.ResponseHeaders?.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
        string? FileOf(Endpoint endpoint)
        {
            var full = Path.GetFullPath(Path.Combine(root, endpoint.AssetFile));
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(full) ? full : null;
        }

        var brotli = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var endpoint in manifest.Endpoints)
        {
            if (endpoint.Selectors is [{ Name: "Content-Encoding", Value: "br" }] && FileOf(endpoint) is { } file)
                brotli["/" + endpoint.Route] = file;
        }
        var assets = new Dictionary<string, Asset>(StringComparer.Ordinal);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var endpoint in manifest.Endpoints)
        {
            // The plain response of each URL; skip encoded variants and the .br/.gz files' own URLs.
            if (endpoint.Selectors is { Count: > 0 } || Header(endpoint, "Content-Encoding") is not null) continue;
            if (FileOf(endpoint) is not { } file) continue;
            var path = "/" + endpoint.Route;
            assets[path] = Describe(path, file, Header(endpoint, "Content-Type") ?? "application/octet-stream", brotli.GetValueOrDefault(path), hashes);
        }
        return assets;
    }

    private static Asset Describe(string path, string file, string contentType, string? brotliPath, Dictionary<string, string> hashes)
    {
        if (!hashes.TryGetValue(file, out var hash))
        {
            using var stream = File.OpenRead(file);
            hashes[file] = hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        return new Asset(path, file, hash, new FileInfo(file).Length, contentType, brotliPath);
    }

    private sealed record EndpointManifest(List<Endpoint> Endpoints);
    private sealed record Endpoint(string Route, string AssetFile, List<NameValue>? Selectors, List<NameValue>? ResponseHeaders);
    private sealed record NameValue(string Name, string Value);

    internal static FileExtensionContentTypeProvider CreateTypes()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".wasm"] = "application/wasm";
        provider.Mappings[".dat"] = "application/octet-stream";
        provider.Mappings[".blat"] = "application/octet-stream";
        provider.Mappings[".webmanifest"] = "application/manifest+json";
        provider.Mappings[".json"] = "application/json";
        provider.Mappings[".mjs"] = "text/javascript";
        provider.Mappings[".js"] = "text/javascript";
        return provider;
    }
}
