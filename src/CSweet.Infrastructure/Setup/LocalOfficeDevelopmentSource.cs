using System.Xml;
using System.Xml.Linq;

namespace CSweet.Infrastructure.Setup;

/// <summary>The same explicitly configured local source used by assisted Windows setup.</summary>
public static class LocalOfficeDevelopmentSource
{
    public static bool IsConfigured(ExecutionFleetOptions options) => OperatingSystem.IsWindows() &&
        options.WindowsDevelopmentLauncherScript is { Length: > 0 } launcher &&
        options.WindowsDevelopmentOfficeBootstrapScript is { Length: > 0 } bootstrap &&
        Path.IsPathFullyQualified(launcher) && Path.IsPathFullyQualified(bootstrap) &&
        File.Exists(launcher) && File.Exists(bootstrap);

    public static bool MatchesHost(string machine, string os, string architecture) =>
        string.Equals(machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(os, "windows", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(architecture, System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            StringComparison.OrdinalIgnoreCase);

    public static string? ReadVersion(ExecutionFleetOptions options)
    {
        if (!IsConfigured(options)) return null;
        var scripts = Path.GetDirectoryName(options.WindowsDevelopmentOfficeBootstrapScript!)!;
        var root = Path.GetFullPath(Path.Combine(scripts, "..", ".."));
        try
        {
            using var reader = XmlReader.Create(Path.Combine(root, "Directory.Build.props"),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
            var versions = XDocument.Load(reader).Descendants("VersionPrefix").Select(x => x.Value.Trim()).ToArray();
            return versions.Length == 1 && Version.TryParse(versions[0], out _) ? versions[0] : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }
    }
}
