using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;

namespace CSweet.Infrastructure.Setup;

public sealed class PluginManifestReader : IPluginManifestReader
{
    public PluginManifestEnvelope Read(ReadOnlyMemory<byte> manifestBytes, string manifestFileName)
    {
        if (!string.Equals(manifestFileName, "csweet-plugin.json", StringComparison.Ordinal))
        {
            throw new JsonException("Legacy manifests are not supported. Use csweet-plugin.json manifestVersion 2.0.");
        }

        var jsonBytes = StripUtf8Bom(manifestBytes);
        using var document = JsonDocument.Parse(jsonBytes);
        var root = document.RootElement;
        var manifestVersion = Required(root, "manifestVersion");
        if (!string.Equals(manifestVersion, "2.0", StringComparison.Ordinal))
        {
            throw new JsonException($"Unsupported plugin manifestVersion '{manifestVersion}'. Expected '2.0'.");
        }

        var kind = Required(root, "kind");
        if (kind is not ("agent" or "service"))
        {
            throw new JsonException("Plugin manifest kind must be 'agent' or 'service'.");
        }

        var manifest = JsonSerializer.Deserialize<PluginManifest>(jsonBytes.Span, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false
        }) ?? throw new JsonException("Plugin manifest is empty.");
        if (!string.Equals(manifest.Protocol.MinimumVersion, "2.0", StringComparison.Ordinal) ||
            !manifest.Protocol.MaximumVersion.StartsWith("2.", StringComparison.Ordinal))
            throw new JsonException("Executable plugins must require MCP runtime protocol 2.0.");
        if (kind == "agent")
        {
            if (manifest.Catalog.Role is not { } role ||
                string.IsNullOrWhiteSpace(role.Key) ||
                string.IsNullOrWhiteSpace(role.Name))
                throw new JsonException("Agent plugins must declare catalog.role.key and catalog.role.name.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(role.Key, "^[a-z0-9]+(?:-[a-z0-9]+)*$"))
                throw new JsonException("Agent plugin catalog.role.key must be a lowercase kebab-case identifier.");
            if (manifest.Catalog.License is not { } license || string.IsNullOrWhiteSpace(license.SpdxId))
                throw new JsonException("Agent plugins must declare catalog.license.spdxId.");
            if (license.SpdxId.Length > 160)
                throw new JsonException("Agent plugin catalog.license.spdxId cannot exceed 160 characters.");
            if (!string.IsNullOrWhiteSpace(license.Url) &&
                (license.Url.Length > 2_048 || !IsHttpsUrl(license.Url)))
                throw new JsonException("Agent plugin catalog.license.url must be an HTTPS URL.");
            if (manifest.Catalog.IconUrls.Count > 4)
                throw new JsonException("Agent plugin catalog.iconUrls cannot contain more than four URLs.");
            if (manifest.Catalog.IconUrls.Any(url => url.Length > 2_048 || !IsHttpsUrl(url)))
                throw new JsonException("Agent plugin catalog.iconUrls must contain only HTTPS URLs.");
        }
        foreach (var capability in manifest.Provides)
        {
            if (string.IsNullOrWhiteSpace(capability.Description) ||
                capability.InputSchema.ValueKind != JsonValueKind.Object ||
                capability.OutputSchema.ValueKind != JsonValueKind.Object ||
                capability.ExecutionTimeoutSeconds is < 1 or > PluginCapabilityDeclaration.MaximumExecutionTimeoutSeconds ||
                capability.Idempotency is not ("work-item" or "caller-key" or "none"))
                throw new JsonException(
                    $"Provided capability '{capability.Name}' must declare description, input/output schemas, " +
                    $"an execution timeout between 1 and {PluginCapabilityDeclaration.MaximumExecutionTimeoutSeconds} seconds, and idempotency.");
        }
        return new PluginManifestEnvelope(
            manifestFileName,
            kind,
            Required(root, "id"),
            Required(root, "name"),
            Required(root, "version"),
            Encoding.UTF8.GetString(jsonBytes.Span));
    }

    private static ReadOnlyMemory<byte> StripUtf8Bom(ReadOnlyMemory<byte> bytes)
        => bytes.Length >= 3 && bytes.Span[0] == 0xEF && bytes.Span[1] == 0xBB && bytes.Span[2] == 0xBF
            ? bytes[3..]
            : bytes;

    private static string Required(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : throw new JsonException($"Plugin manifest property '{propertyName}' is required.");

    private static bool IsHttpsUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
