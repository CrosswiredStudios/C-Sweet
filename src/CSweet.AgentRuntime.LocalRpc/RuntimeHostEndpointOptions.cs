namespace CSweet.AgentRuntime.LocalRpc;

public sealed class RuntimeHostEndpointOptions
{
    public const string SectionName = "CSweet:AgentRuntime:RuntimeHost";

    public string NamedPipeName { get; set; } = "csweet-runtime-host-v1";
    public string? AllowedClientSid { get; set; }
    public string[] AllowedClientSids { get; set; } = [];
    public string UnixSocketPath { get; set; } = "/var/run/csweet/runtime-host-v1.sock";
    public int ConnectTimeoutSeconds { get; set; } = 2;
    public int MaximumFrameBytes { get; set; } = 1024 * 1024;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(NamedPipeName) ||
            NamedPipeName.Length > 100 ||
            NamedPipeName.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new InvalidOperationException("The runtime-host pipe name is invalid.");
        if (string.IsNullOrWhiteSpace(UnixSocketPath) ||
            !IsPortableUnixAbsolutePath(UnixSocketPath) ||
            UnixSocketPath.Length > 200)
            throw new InvalidOperationException("The runtime-host Unix socket path must be a bounded absolute path.");
        if (ConnectTimeoutSeconds is < 1 or > 120)
            throw new InvalidOperationException("The runtime-host connection timeout is invalid.");
        if (MaximumFrameBytes is < 4096 or > 16 * 1024 * 1024)
            throw new InvalidOperationException("The runtime-host maximum frame size is invalid.");
    }

    private static bool IsPortableUnixAbsolutePath(string path) =>
        (path.Length > 0 && path[0] == '/' || OperatingSystem.IsWindows() && Path.IsPathFullyQualified(path)) &&
        !path.Contains('\0') &&
        !path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or "..");
}
