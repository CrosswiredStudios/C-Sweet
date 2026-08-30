using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Contracts.Plugins;

namespace CSweet.Infrastructure.WorkManagement;

public sealed record ValidatedToolchainAdapterDefinition(
    string Key, int Version, string DisplayName, string DefinitionJson, string Digest);

public static class ToolchainAdapterDefinitionValidator
{
    public const int MaximumDefinitionBytes = 256 * 1024;

    public static ValidatedToolchainAdapterDefinition Validate(
        PluginToolchainAdapterContribution contribution, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumDefinitionBytes)
            throw new ArgumentException("Toolchain adapter definition must be between 1 byte and 256 KB.");
        using var document = JsonDocument.Parse(bytes.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("Toolchain adapter definition must be an object.");
        var key = Required(root, "key", 200); var displayName = Required(root, "displayName", 256);
        var version = root.TryGetProperty("version", out var versionValue) && versionValue.TryGetInt32(out var parsed) ? parsed : 0;
        if (key != contribution.Key || version != contribution.Version || version < 1)
            throw new ArgumentException("Toolchain adapter key and version must match its manifest contribution.");
        RequireObject(root, "requiredExecutableVersions");
        ValidateDependencyRegistries(root);
        ValidateOutputPolicy(root.GetProperty("outputPolicy"));
        if (root.TryGetProperty("requiredArtifactDigests", out var requiredArtifacts))
            ValidateRequiredArtifactDigests(requiredArtifacts);
        RequireStringArray(root, "supportedContentTypes", 64); RequireStringArray(root, "previewModes", 16);
        if (!root.TryGetProperty("recipes", out var recipes) || recipes.ValueKind != JsonValueKind.Array || recipes.GetArrayLength() is 0 or > 32)
            throw new ArgumentException("Toolchain adapter definitions require one to 32 recipes.");
        var recipeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var recipe in recipes.EnumerateArray())
        {
            if (recipe.ValueKind != JsonValueKind.Object || !recipeKeys.Add(Required(recipe, "key", 200)))
                throw new ArgumentException("Recipe keys must be unique.");
            RequireStringArray(recipe, "operations", 16); RequireStringArray(recipe, "targetKeys", 32);
            RequireStringArray(recipe, "requiredEnvironmentProfileKeys", 16); RequireObject(recipe, "configurationSchema");
            if (!recipe.TryGetProperty("certificationFixtures", out var fixtures) || fixtures.ValueKind != JsonValueKind.Array || fixtures.GetArrayLength() is 0 or > 16)
                throw new ArgumentException("Every recipe requires one to 16 certification fixtures.");
            foreach (var fixture in fixtures.EnumerateArray())
            {
                Required(fixture, "key", 200); var resource = Required(fixture, "resource", 512);
                ValidateResource(resource); RequireStringArray(fixture, "expectedCheckKeys", 32);
            }
        }
        var canonical = JsonSerializer.Serialize(root, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new(key, version, displayName, canonical, digest);
    }

    private static void RequireObject(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"Toolchain adapter property '{name}' must be an object.");
    }

    private static void ValidateDependencyRegistries(JsonElement root)
    {
        if (!root.TryGetProperty("allowedDependencyRegistryHosts", out var hosts)) return;
        if (hosts.ValueKind != JsonValueKind.Array || hosts.GetArrayLength() > 16)
            throw new ArgumentException("Toolchain allowedDependencyRegistryHosts must be an array with no more than 16 entries.");
        var values = hosts.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).ToArray();
        if (values.Any(x => string.IsNullOrWhiteSpace(x) || x!.Length > 253 ||
                !Uri.TryCreate("https://" + x, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Host, x, StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort) ||
            values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
            throw new ArgumentException("Toolchain dependency registry hosts must be unique bounded DNS host names without schemes or ports.");
    }

    private static void ValidateRequiredArtifactDigests(JsonElement artifacts)
    {
        if (artifacts.ValueKind != JsonValueKind.Object || artifacts.EnumerateObject().Count() is 0 or > 64)
            throw new ArgumentException("Toolchain requiredArtifactDigests must be an object with one to 64 entries.");
        foreach (var artifact in artifacts.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(artifact.Name) || artifact.Name.Length > 200 || artifact.Value.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Every required toolchain artifact must have a bounded key and object value.");
            var sha256 = Required(artifact.Value, "sha256", 64);
            if (sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))
                throw new ArgumentException($"Toolchain artifact '{artifact.Name}' requires an exact SHA-256 digest.");
            if (artifact.Value.TryGetProperty("source", out var source) &&
                (source.ValueKind != JsonValueKind.String || !Uri.TryCreate(source.GetString(), UriKind.Absolute, out var uri) ||
                 uri.Scheme != Uri.UriSchemeHttps || source.GetString()!.Length > 2048))
                throw new ArgumentException($"Toolchain artifact '{artifact.Name}' source must be a bounded HTTPS URL.");
        }
    }

    private static void ValidateOutputPolicy(JsonElement policy)
    {
        if (policy.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Toolchain outputPolicy must be an object.");
        var maximumFileCount = PositiveInt64(policy, "maximumFileCount", 10_000);
        var maximumFileBytes = PositiveInt64(policy, "maximumFileBytes", 4L * 1024 * 1024 * 1024);
        var maximumTotalBytes = PositiveInt64(policy, "maximumTotalBytes", 16L * 1024 * 1024 * 1024);
        if (maximumFileCount < 1 || maximumFileBytes < 1 || maximumTotalBytes < maximumFileBytes)
            throw new ArgumentException("Toolchain outputPolicy limits are inconsistent.");
        if (!policy.TryGetProperty("denySymlinks", out var symlinks) || symlinks.ValueKind != JsonValueKind.True ||
            !policy.TryGetProperty("denyAbsolutePaths", out var absolutePaths) || absolutePaths.ValueKind != JsonValueKind.True)
            throw new ArgumentException("Toolchain outputPolicy must deny symlinks and absolute paths.");
    }

    private static long PositiveInt64(JsonElement root, string name, long maximum)
    {
        if (!root.TryGetProperty(name, out var value) || !value.TryGetInt64(out var parsed) || parsed is < 1 || parsed > maximum)
            throw new ArgumentException($"Toolchain outputPolicy '{name}' must be between 1 and {maximum}.");
        return parsed;
    }

    private static void RequireStringArray(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() is 0 || value.GetArrayLength() > maximum ||
            value.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(x.GetString())) ||
            value.EnumerateArray().Select(x => x.GetString()).Distinct(StringComparer.Ordinal).Count() != value.GetArrayLength())
            throw new ArgumentException($"Toolchain adapter property '{name}' must contain unique non-empty strings.");
    }

    private static string Required(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > maximum)
            throw new ArgumentException($"Toolchain adapter property '{name}' is required and cannot exceed {maximum} characters.");
        return value.GetString()!;
    }

    private static void ValidateResource(string path)
    {
        if (Path.IsPathRooted(path) || path.StartsWith('/') || path.StartsWith('\\') ||
            path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Contains("..", StringComparer.Ordinal))
            throw new ArgumentException("Certification fixture resources must be bounded relative paths.");
    }
}
