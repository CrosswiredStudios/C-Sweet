using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace CSweet.UI.Localization;

public sealed class AppLocalizer(LocalizationResources resources, AppCulture culture, ILogger<AppLocalizer>? logger = null) : IAppLocalizer
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();
    private readonly HashSet<string> _reportedMissing = [];

    public string this[string key] => Resolve(key) ?? key;

    public string Format(string key, object? args)
    {
        var pattern = Resolve(key);
        if (pattern is null)
        {
            return key;
        }

        return IcuMessageFormatter.Format(pattern, ToDictionary(args), culture.Culture);
    }

    public bool Has(string key) => Resolve(key, reportMissing: false) is not null;

    private string? Resolve(string key, bool reportMissing = true)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        if (resources.GetTable(culture.CurrentCode).TryGetValue(key, out var value))
        {
            return value;
        }

        if (!string.Equals(culture.CurrentCode, AppCulture.DefaultCode, StringComparison.OrdinalIgnoreCase) &&
            resources.GetTable(AppCulture.DefaultCode).TryGetValue(key, out var fallback))
        {
            return fallback;
        }

        if (reportMissing && _reportedMissing.Add(key))
        {
            // Blazor treats stderr output as a runtime failure and opens its global error UI.
            // A missing translation has a safe fallback and is only a warning.
            logger?.LogWarning("Missing localization resource key '{Key}' ({Culture})", key, culture.CurrentCode);
        }

        return null;
    }

    private static IReadOnlyDictionary<string, object?>? ToDictionary(object? args)
    {
        switch (args)
        {
            case null:
                return null;
            case IReadOnlyDictionary<string, object?> ready:
                return ready;
            case IDictionary<string, object?> dictionary:
                return new Dictionary<string, object?>(dictionary);
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                return pairs.ToDictionary(p => p.Key, p => p.Value);
        }

        var properties = PropertyCache.GetOrAdd(args.GetType(), static t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance));
        var result = new Dictionary<string, object?>(properties.Length, StringComparer.Ordinal);
        foreach (var property in properties)
        {
            if (property.GetIndexParameters().Length == 0)
            {
                result[property.Name] = property.GetValue(args);
            }
        }

        return result;
    }
}
