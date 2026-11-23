using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.Hosting;

internal static class WindowsHeadquartersSettings
{
    private const string SourceControl = "CSweet:SourceControl:";

    internal static void Apply(IConfigurationManager configuration, string path)
    {
        if (!File.Exists(path))
            return;

        var defaults = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("Missing installer-generated Headquarters settings.");

        // A deployment may supply an external credential file instead of an inline key.
        if (!string.IsNullOrWhiteSpace(configuration["TrustedServiceAuthentication:KeyFile"]))
            defaults.Remove("TrustedServiceAuthentication:SharedKeyBase64");
        if (!string.IsNullOrWhiteSpace(configuration[SourceControl + "TrustedServiceKeyFile"])
            || HasRemoteEndpoint("GitHostBaseUrl") || HasRemoteEndpoint("ProvisionerHostBaseUrl"))
            defaults.Remove(SourceControl + "TrustedServiceKeyBase64");
        if (HasRemoteEndpoint("CoreBrokerBaseUrl"))
            defaults.Remove(SourceControl + "AgentBrokerKeyBase64");
        if (configuration.GetSection("Kestrel:Endpoints").Exists() || configuration["HTTP_PORTS"] != null
            || configuration["HTTPS_PORTS"] != null)
            defaults.Remove("Urls");

        configuration.AddInMemoryCollection(defaults.Where(setting => configuration[setting.Key] is null)
            .Select(setting => KeyValuePair.Create<string, string?>(setting.Key, setting.Value)).ToArray());

        bool HasRemoteEndpoint(string name) => configuration[SourceControl + name] is { Length: > 0 } value
            && !(Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsLoopback);
    }
}
