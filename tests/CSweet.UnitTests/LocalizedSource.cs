using System.Text.Json;
using System.Text.RegularExpressions;

namespace CSweet.UnitTests;

/// <summary>
/// Source-text assertions that understand localized Razor: a label counts as present when the
/// source contains it literally or references a resource key whose English value contains it.
/// </summary>
internal static partial class LocalizedSource
{
    private static readonly Lazy<Dictionary<string, string>> English = new(LoadEnglish);

    public static void AssertContains(string expected, string source)
    {
        if (source.Contains(expected, StringComparison.Ordinal)) return;
        foreach (Match match in KeyReference().Matches(source))
        {
            if (English.Value.TryGetValue(match.Groups["key"].Value, out var value) &&
                value.Contains(expected, StringComparison.Ordinal)) return;
        }

        Assert.Fail($"'{expected}' is neither in the source nor in an English resource the source references.");
    }

    private static Dictionary<string, string> LoadEnglish()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CSweet.slnx"))) directory = directory.Parent;
        var resources = Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("CSweet.slnx not found."),
            "src", "CSweet.UI", "Localization", "Resources");
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(resources, "en.json", SearchOption.AllDirectories))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.String) table[property.Name] = property.Value.GetString()!;
        }

        return table;
    }

    [GeneratedRegex(@"L(?:\[|\.Format\(|\.Has\()\s*""(?<key>[A-Za-z0-9_.-]+)""")]
    private static partial Regex KeyReference();
}
