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
        var assets = new Dictionary<string, Asset>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".br", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) continue;
            var relative = "/" + Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/..", StringComparison.Ordinal)) continue;
            using var stream = File.OpenRead(file);
            var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            var type = Types.TryGetContentType(file, out var known) ? known : "application/octet-stream";
            var brotli = File.Exists(file + ".br") ? file + ".br" : null;
            assets[relative] = new Asset(relative, file, hash, new FileInfo(file).Length, type, brotli);
        }
        logger.LogInformation("Mobile shell: {Count} published web files from {Root}.", assets.Count, root);
        return assets;
    }

    private static FileExtensionContentTypeProvider CreateTypes()
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
