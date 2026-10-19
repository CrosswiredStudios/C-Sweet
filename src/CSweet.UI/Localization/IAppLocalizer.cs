namespace CSweet.UI.Localization;

/// <summary>
/// Resolves UI strings for the current culture. Inject as <c>@inject IAppLocalizer L</c> and use
/// <c>@L["Home.Title"]</c> for plain strings and <c>@L.Format("Board.ItemCount", new { count })</c>
/// for ICU messages with arguments. Missing keys fall back to English, then to the key itself.
/// </summary>
public interface IAppLocalizer
{
    string this[string key] { get; }

    /// <summary>Formats an ICU message. <paramref name="args"/> is an anonymous object or a dictionary.</summary>
    string Format(string key, object? args);

    bool Has(string key);
}
