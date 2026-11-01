using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace CSweet.Infrastructure.Setup;

/// <summary>Shares only deployment-client configuration, never owner or provider credentials.</summary>
internal static class ManagedConnectionHostConfiguration
{
    internal const string Section = "CSweet:PluginConnections:Managed";
    internal const string HashKey = "CSweet:PluginConnections:ManagedConfigurationHash";
    private const string Prefix = "CSweet__PluginConnections__Managed__";
    private const string Invalid = "managed_shared_configuration_invalid";
    private const string Conflict = "managed_shared_configuration_conflict";

    internal static IReadOnlyDictionary<string, string> ReadEnvironment(IConfiguration configuration)
    {
        var section = configuration.GetSection(Section);
        if (!section.Exists()) return new Dictionary<string, string>(); // Standalone/legacy configuration is unchanged.
        var allowed = new[] { "Enabled", "ServiceOrigin", "ReturnUri", "RegistrationPath", "CustomerOwnedProfiles" };
        if (section.GetChildren().Any(x => !allowed.Contains(x.Key, StringComparer.OrdinalIgnoreCase)) ||
            !bool.TryParse(section["Enabled"], out var enabled)) throw new InvalidOperationException(Invalid);
        string Required(string key) => section[key] is { Length: > 0 and <= 4096 } value && !value.Any(char.IsControl)
            ? value : throw new InvalidOperationException(Invalid);
        var origin = Required("ServiceOrigin"); var target = Required("ReturnUri"); var path = Required("RegistrationPath");
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var service) || service.Scheme != "https" ||
            service.AbsolutePath != "/" || service.UserInfo.Length != 0 || service.Query.Length != 0 || service.Fragment.Length != 0 ||
            !Uri.TryCreate(target, UriKind.Absolute, out var browser) ||
            !(browser.Scheme == "https" || browser.Scheme == "http" && browser.IsLoopback) ||
            browser.AbsolutePath != "/settings/plugins/managed-return" || browser.UserInfo.Length != 0 ||
            browser.Query.Length != 0 || browser.Fragment.Length != 0 || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException(Invalid);
        origin = service.GetLeftPart(UriPartial.Authority);
        var children = section.GetSection("CustomerOwnedProfiles").GetChildren().ToArray();
        if (section["CustomerOwnedProfiles"] is { Length: > 0 } || children.Length > 32 ||
            children.Where((x, index) => x.Key != index.ToString(CultureInfo.InvariantCulture)).Any())
            throw new InvalidOperationException(Invalid);
        var profiles = children.Select(x => x.Value ?? throw new InvalidOperationException(Invalid)).ToArray();
        if (profiles.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 256 || x.Any(char.IsControl)) ||
            profiles.Distinct(StringComparer.Ordinal).Count() != profiles.Length)
            throw new InvalidOperationException(Invalid);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Prefix + "Enabled"] = enabled ? "true" : "false", [Prefix + "ServiceOrigin"] = origin,
            [Prefix + "ReturnUri"] = target, [Prefix + "RegistrationPath"] = path,
            [HashKey.Replace(":", "__")] = Hash(enabled, origin, target, path, profiles)
        };
        for (var i = 0; i < profiles.Length; i++) values[Prefix + "CustomerOwnedProfiles__" + i.ToString(CultureInfo.InvariantCulture)] = profiles[i]!;
        return values;
    }

    // Normal configuration providers retain their precedence. If a higher-priority child override
    // conflicts, or lower-priority array entries survive binding, refuse to use a different authority.
    internal static void RequireMatch(string? expectedHash, bool enabled, string origin, string target, string path, string[] profiles)
    {
        if (expectedHash is null) return;
        if (expectedHash.Length != 64 || !expectedHash.All(char.IsAsciiHexDigit) ||
            expectedHash != Hash(enabled, origin, target, path, profiles)) throw new InvalidOperationException(Conflict);
    }

    private static string Hash(bool enabled, string origin, string target, string path, IEnumerable<string> profiles) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new[] { enabled ? "true" : "false", origin, target, path }.Concat(profiles.Order(StringComparer.Ordinal))))));
}
