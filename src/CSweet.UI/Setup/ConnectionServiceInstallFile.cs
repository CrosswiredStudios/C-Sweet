using CSweet.Contracts.Plugins;

namespace CSweet.UI.Setup;

internal static class ConnectionServiceInstallFile
{
    private static readonly Lazy<string> Template = new(() =>
    {
        using var stream = typeof(ConnectionServiceInstallFile).Assembly.GetManifestResourceStream("CSweet.ConnectionService.Compose")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string? Build(string address, string browserReturn)
    {
        string origin;
        try { origin = ManagedServiceTrust.CanonicalOrigin(address); }
        catch (InvalidOperationException) { return null; }
        var uri = new Uri(origin);
        // Caddy obtains a public certificate. Only a DNS name can enter the generated YAML.
        if (!uri.IsDefaultPort || uri.HostNameType != UriHostNameType.Dns || uri.IsLoopback ||
            !uri.IdnHost.Contains('.') || !uri.IdnHost.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.'))
            return null;
        var localReturn = "";
        if (Uri.TryCreate(browserReturn, UriKind.Absolute, out var target) && target.Scheme == "https" &&
            target.IsLoopback && target.UserInfo.Length == 0 && target.Query.Length == 0 && target.Fragment.Length == 0 &&
            target.AbsolutePath.EndsWith("/settings/plugins/managed-return", StringComparison.Ordinal) &&
            target.AbsolutePath.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_'))
            localReturn = $"ConnectionService__AllowedLocalBrowserReturnUris__0: '{target.AbsoluteUri}'";
        return Template.Value.Replace("SERVICE_ORIGIN", "https://" + uri.IdnHost, StringComparison.Ordinal)
            .Replace("# LOCAL_RETURN", localReturn, StringComparison.Ordinal);
    }

    public static string DownloadUrl(string contents) =>
        "data:text/plain;charset=utf-8;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(contents));
}
