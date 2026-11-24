namespace CSweet.UI.Setup;

internal static class MacOfficeCommand
{
    public static string Format(string gateway, string? certificateSha256)
    {
        var quotedGateway = gateway.Replace("'", "'\"'\"'", StringComparison.Ordinal);
        var command = $"sudo /usr/local/sbin/csweet-configure-office '{quotedGateway}'";
        return certificateSha256 is { Length: 64 } && certificateSha256.All(Uri.IsHexDigit)
            ? $"{command} --gateway-sha256 {certificateSha256}"
            : command;
    }
}
