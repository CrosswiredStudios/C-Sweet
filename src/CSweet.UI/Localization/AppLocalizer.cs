using System.Collections.Concurrent;
using System.Reflection;

namespace CSweet.UI.Localization;

public sealed class AppLocalizer(LocalizationResources resources, AppCulture culture) : IAppLocalizer
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

    public bool Has(string key) => Resolve(key) is not null;

    private string? Resolve(string key)
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

        if (_reportedMissing.Add(key))
        {
            Console.Error.WriteLine($"[localization] missing resource key '{key}' ({culture.CurrentCode})");
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
