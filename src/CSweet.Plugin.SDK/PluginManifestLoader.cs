using System.Text.Json;
using CSweet.Contracts.Plugins;

namespace CSweet.Plugin.SDK;

public static class PluginManifestLoader
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static async Task<PluginManifest> LoadAsync(string? path = null, CancellationToken cancellationToken = default)
    {
        path ??= Environment.GetEnvironmentVariable("CSweet__Plugin__ManifestPath")
            ?? Environment.GetEnvironmentVariable("CSweet__Agent__ManifestPath")
            ?? Path.Combine(AppContext.BaseDirectory, "csweet-plugin.json");
        if (!string.Equals(Path.GetFileName(path), "csweet-plugin.json", StringComparison.Ordinal))
            throw new InvalidOperationException("The canonical plugin manifest must be named csweet-plugin.json.");
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<PluginManifest>(stream, Options, cancellationToken)
            ?? throw new InvalidOperationException("Plugin manifest is empty.");
        if (manifest.ManifestVersion != "2.0" ||
            manifest.Kind is not ("agent" or "service") ||
            manifest.Protocol.MinimumVersion != "2.0" ||
            !manifest.Protocol.MaximumVersion.StartsWith("2.", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Plugin manifest must use manifestVersion 2.0, MCP runtime protocol 2.x, and kind agent or service.");
        if (manifest.Provides.Any(x =>
                string.IsNullOrWhiteSpace(x.Description) ||
                x.InputSchema.ValueKind != JsonValueKind.Object ||
                x.OutputSchema.ValueKind != JsonValueKind.Object ||
                x.ExecutionTimeoutSeconds is < 1 or > 900))
            throw new InvalidOperationException("Every provided capability must include schemas and a bounded execution timeout.");
        return manifest;
    }
}
