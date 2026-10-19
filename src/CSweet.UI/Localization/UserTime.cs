using System.Globalization;

namespace CSweet.UI.Localization;

/// <summary>
/// Formats instants in the user's time zone and culture. Prefer this over <c>ToLocalTime().ToString("g")</c>
/// in components so an explicit time-zone preference is honored and formats follow the active culture.
/// </summary>
public sealed class UserTime(AppCulture culture)
{
    public DateTimeOffset ToUser(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, culture.TimeZone);

    public DateTimeOffset ToUser(DateTime value)
    {
        var instant = value.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(value, TimeSpan.Zero),
            DateTimeKind.Local => new DateTimeOffset(value),
            _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero)
        };
        return ToUser(instant);
    }

    /// <summary>Standard .NET format strings ("g", "d", "t", "f", …) resolved against the active culture.</summary>
    public string Format(DateTimeOffset value, string format = "g") => ToUser(value).ToString(format, culture.Culture);

    public string Format(DateTime value, string format = "g") => ToUser(value).ToString(format, culture.Culture);

    public string Format(DateTimeOffset? value, string format = "g", string fallback = "") =>
        value.HasValue ? Format(value.Value, format) : fallback;

    public string Format(DateTime? value, string format = "g", string fallback = "") =>
        value.HasValue ? Format(value.Value, format) : fallback;

    public string FormatNumber(long value, string format = "N0") => value.ToString(format, culture.Culture);

    public string FormatNumber(double value, string format = "N0") => value.ToString(format, culture.Culture);

    public string FormatNumber(decimal value, string format = "N2") => value.ToString(format, culture.Culture);

    public CultureInfo Culture => culture.Culture;
}
