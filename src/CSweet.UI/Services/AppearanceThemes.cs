using System.Text.Json;
using MudBlazor;

namespace CSweet.UI.Services;

public sealed record AppearanceTheme(string Id, string Name, string Description, string Family,
    bool IsDark, Dictionary<string, string> Tokens)
{
    public string PreviewStyle => string.Join(";", Tokens.Select(t => $"--preview-{t.Key}:{t.Value}"));

    public MudTheme CreateMudTheme()
    {
        var theme = new MudTheme();
        Configure(theme.PaletteLight);
        Configure(theme.PaletteDark);
        theme.LayoutProperties.DrawerWidthLeft = "260px";
        return theme;
    }

    private void Configure(Palette palette)
    {
        palette.Primary = Tokens["warm"];
        palette.PrimaryContrastText = ContrastText(Tokens["warm"]);
        palette.Secondary = Tokens["accent-2"];
        palette.SecondaryContrastText = ContrastText(Tokens["accent-2"]);
        palette.Tertiary = Tokens["accent-3"];
        palette.TertiaryContrastText = ContrastText(Tokens["accent-3"]);
        palette.Background = Tokens["paper"];
        palette.BackgroundGray = Tokens["elevated"];
        palette.Surface = Tokens["surface"];
        palette.TextPrimary = Tokens["ink"];
        palette.TextSecondary = Tokens["muted"];
        palette.TextDisabled = Tokens["faint"];
        palette.AppbarBackground = Tokens["surface"];
        palette.AppbarText = Tokens["ink"];
        palette.DrawerBackground = Tokens["surface"];
        palette.DrawerText = Tokens["ink"];
        palette.DrawerIcon = Tokens["muted"];
        palette.ActionDefault = Tokens["muted"];
        palette.LinesDefault = Tokens["edge"];
        palette.LinesInputs = Tokens["edge-strong"];
        palette.Divider = Tokens["edge"];
        palette.Success = Tokens["ok"];
        palette.SuccessContrastText = ContrastText(Tokens["ok"]);
        palette.Warning = Tokens["warm"];
        palette.WarningContrastText = ContrastText(Tokens["warm"]);
        palette.Error = Tokens["danger"];
        palette.ErrorContrastText = ContrastText(Tokens["danger"]);
        palette.Info = Tokens["accent-2"];
        palette.InfoContrastText = ContrastText(Tokens["accent-2"]);
    }

    // Choose the stronger black/white contrast; matches the generated CSS actions.
    private static string ContrastText(string color)
    {
        static double Channel(string hex)
        {
            var s = Convert.ToInt32(hex, 16) / 255d;
            return s <= .03928 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4);
        }
        var l = .2126 * Channel(color.Substring(1, 2)) + .7152 * Channel(color.Substring(3, 2)) + .0722 * Channel(color.Substring(5, 2));
        return l > .179 ? "#000000" : "#ffffff";
    }
}

public static class AppearanceThemes
{
    public const string DefaultId = "night-glass";
    public static IReadOnlyList<AppearanceTheme> All { get; } = Load();
    public static AppearanceTheme Default => All[0];
    public static string Normalize(string? id) => id switch
    {
        // Upstream's two original themes keep their scheme: Aurora Light was light, Black & Gold dark.
        "night" => DefaultId,
        "day" or "aurora" => "csweet-daylight",
        "gold" => "signature-gold",
        _ => id ?? DefaultId
    };
    public static AppearanceTheme? Find(string? id)
    {
        var normalized = Normalize(id);
        return All.FirstOrDefault(t => t.Id == normalized);
    }

    public static AppearanceTheme ResolveStored(string? id)
    {
        if (Find(id) is { } current) return current;
        // Older catalog IDs put a collection prefix before the palette ID.
        if (id is null) return Default;
        var separator = id.IndexOf('-');
        if (separator <= 0) return Default;
        var palette = id[(separator + 1)..];
        if (palette == "gold") palette = "signature-gold";
        return All.FirstOrDefault(t => t.Id == palette) ?? Default;
    }
    public static AppearanceTheme Resolve(string? id) => Find(id) ?? Default;

    private static IReadOnlyList<AppearanceTheme> Load()
    {
        using var stream = typeof(AppearanceThemes).Assembly.GetManifestResourceStream("CSweet.AppearanceThemes.json")
            ?? throw new InvalidOperationException("The appearance catalog is missing.");
        return JsonSerializer.Deserialize<List<AppearanceTheme>>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.AsReadOnly();
    }
}
