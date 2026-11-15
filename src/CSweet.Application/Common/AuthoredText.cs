namespace CSweet.Application.Common;

/// <summary>Content authored by people or agents is text, not a bounded identifier.</summary>
public static class AuthoredText
{
    public static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string Required(string? value, string name) =>
        Optional(value) ?? throw new ArgumentException($"{name} is required.");
}
