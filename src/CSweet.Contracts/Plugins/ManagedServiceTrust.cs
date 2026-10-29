namespace CSweet.Contracts.Plugins;

/// <summary>Application-owned origin policy; remote names and protocol headers convey no official identity.</summary>
public static class ManagedServiceTrust
{
    public const string OfficialOrigin = "https://connections.c-sweet.com";

    public static string CanonicalOrigin(string value)
    {
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        var firstPath = scheme < 0 ? -1 : value.IndexOf('/', scheme + 3);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.Host.EndsWith('.') || value.Any(char.IsControl) ||
            value != value.Trim() || value.Contains('\\') || firstPath >= 0 && firstPath != value.Length - 1)
            throw new InvalidOperationException("An exact HTTPS service origin is required.");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public static string Identity(string? value)
    {
        try { return CanonicalOrigin(value ?? "") == OfficialOrigin ? "Official" : "Custom"; }
        catch (InvalidOperationException) { return "Unconfigured"; }
    }
}
