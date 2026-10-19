using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace CSweet.UI.Localization;

/// <summary>
/// Loads the embedded resource files under <c>Localization/Resources/&lt;Area&gt;/&lt;culture&gt;.json</c>.
/// Every area file is a flat JSON object of key → ICU message. Keys starting with "_" are translator notes
/// and are ignored. All areas for one culture are merged into a single dictionary; keys are therefore
/// global and conventionally prefixed with the component name ("Home.Title", "Common.Save").
/// </summary>
public sealed class LocalizationResources
{
    private const string Prefix = "i18n/";
    private static readonly Assembly ResourceAssembly = typeof(LocalizationResources).Assembly;
    private static readonly Lazy<IReadOnlyList<ResourceFile>> Files = new(DiscoverFiles);
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public sealed record ResourceFile(string Area, string Culture, string ManifestName);

    /// <summary>Culture codes that have at least one area file.</summary>
    public static IReadOnlyCollection<string> AvailableCultures =>
        Files.Value.Select(f => f.Culture).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static IReadOnlyCollection<string> Areas =>
        Files.Value.Select(f => f.Area).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Merged key → message table for one culture (no fallback applied).</summary>
    public IReadOnlyDictionary<string, string> GetTable(string culture) =>
        _cache.GetOrAdd(culture, Load);

    private static IReadOnlyDictionary<string, string> Load(string culture)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Files.Value.Where(f => string.Equals(f.Culture, culture, StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = ResourceAssembly.GetManifestResourceStream(file.ManifestName);
            if (stream is null)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Name.StartsWith('_') || property.Value.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    table[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"[localization] {file.Area}/{file.Culture}.json is not valid JSON: {ex.Message}");
            }
        }

        return table;
    }

    private static IReadOnlyList<ResourceFile> DiscoverFiles()
    {
        var files = new List<ResourceFile>();
        foreach (var name in ResourceAssembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // i18n/<Area>/<culture>.json — RecursiveDir uses the host separator at build time.
            var relative = name[Prefix.Length..^".json".Length].Replace('\\', '/');
            var slash = relative.LastIndexOf('/');
            if (slash <= 0)
            {
                continue;
            }

            files.Add(new ResourceFile(relative[..slash], relative[(slash + 1)..], name));
        }

        return files;
    }
}
