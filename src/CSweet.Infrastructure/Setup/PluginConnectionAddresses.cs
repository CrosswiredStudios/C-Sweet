namespace CSweet.Infrastructure.Setup;

/// <summary>OAuth destinations come from deployment configuration, never callback query parameters.</summary>
public static class PluginConnectionAddresses
{
    public static string Callback(PluginConnectionOptions options) =>
        Base(options.PublicBaseUrl, false) + "/api/plugin-connections/oauth/callback";

    public static string Return(PluginConnectionOptions options, string path, bool development = false)
    {
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\\') ||
            path.Any(char.IsControl))
            throw new ArgumentException("A platform-owned relative return path is required.", nameof(path));
        return string.IsNullOrWhiteSpace(options.ReturnBaseUrl) ? path : Base(options.ReturnBaseUrl, development) + path;
    }

    private static string Base(string? value, bool development)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == Uri.UriSchemeHttps || development && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            value.Any(char.IsControl) || value.Contains('\\'))
            throw new InvalidOperationException("Configure valid public HTTPS connection addresses. Only the development UI may use HTTP on loopback.");
        return uri.AbsoluteUri.TrimEnd('/');
    }
}
