namespace CSweet.Contracts.Localization;

/// <summary>A UI culture C-Sweet ships resources for.</summary>
/// <param name="Code">BCP-47 tag used for resource files and <c>CultureInfo</c>.</param>
/// <param name="EnglishName">Name shown in English contexts (logs, admin tooling).</param>
/// <param name="NativeName">Name shown in the language picker, in the language itself.</param>
/// <param name="RightToLeft">Whether the layout direction is right-to-left.</param>
public sealed record SupportedCulture(string Code, string EnglishName, string NativeName, bool RightToLeft);

/// <summary>
/// The single source of truth for the languages the workspace can be displayed in and that agents can be
/// asked to write in. The API validates preferences against this list; the UI builds its picker from it.
/// Adding a language means adding it here and providing resource files for it.
/// </summary>
public static class SupportedCultures
{
    public const string Default = "en";

    public static readonly IReadOnlyList<SupportedCulture> All =
    [
        new("en", "English", "English", false),
        new("zh-Hans", "Chinese (Simplified)", "简体中文", false),
        new("es", "Spanish", "Español", false),
        new("ja", "Japanese", "日本語", false),
        new("de", "German", "Deutsch", false),
        new("fr", "French", "Français", false),
        new("pt-BR", "Portuguese (Brazil)", "Português (Brasil)", false),
        new("ar", "Arabic", "العربية", true),
        new("ru", "Russian", "Русский", false),
        new("ko", "Korean", "한국어", false),
    ];

    public static SupportedCulture Find(string code) =>
        All.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))
        ?? All[0];

    public static bool IsSupported(string? code) =>
        code is not null && All.Any(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Maps a browser or OS language tag onto a supported culture code, or null when nothing fits.
    /// <c>en-GB</c> → <c>en</c>, <c>es-MX</c> → <c>es</c>, <c>pt-PT</c> → <c>pt-BR</c>,
    /// <c>zh-CN</c>/<c>zh-SG</c>/<c>zh-Hans-*</c> → <c>zh-Hans</c>. Traditional Chinese is not mapped.
    /// </summary>
    public static string? Normalize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        tag = tag.Trim().Replace('_', '-');
        var exact = All.FirstOrDefault(x => string.Equals(x.Code, tag, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact.Code;
        }

        var parts = tag.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var language = parts[0].ToLowerInvariant();
        if (language == "zh")
        {
            var isTraditional = parts.Skip(1).Any(p =>
                p.Equals("Hant", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("TW", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("HK", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("MO", StringComparison.OrdinalIgnoreCase));
            return isTraditional ? null : "zh-Hans";
        }

        if (language == "pt")
        {
            return "pt-BR";
        }

        var byLanguage = All.FirstOrDefault(x => string.Equals(x.Code, language, StringComparison.OrdinalIgnoreCase));
        return byLanguage?.Code;
    }
}
