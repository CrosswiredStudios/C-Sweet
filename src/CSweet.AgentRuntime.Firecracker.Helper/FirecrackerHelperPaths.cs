namespace CSweet.AgentRuntime.Firecracker.Helper;

internal sealed record FirecrackerHelperPaths(
    string DataRoot,
    string InstancesRoot,
    string JailerRoot,
    string ArtifactMediaRoot,
    string FirecrackerExecutable,
    string JailerExecutable,
    string KernelImage,
    string InitrdImage,
    uint WorkloadUid,
    uint WorkloadGid,
    uint GuestVsockPort,
    string ParentCgroup)
{
    public static FirecrackerHelperPaths Resolve()
    {
        var dataRoot = AbsoluteEnvironment("CSWEET_FIRECRACKER_DATA_ROOT", "/var/lib/csweet/runtime-host/firecracker");
        var packageRoot = AbsoluteEnvironment("CSWEET_FIRECRACKER_PACKAGE_ROOT", "/opt/csweet/execution/firecracker");
        var artifactRoot = AbsoluteEnvironment("CSWEET_ARTIFACT_MEDIA_ROOT", "/var/lib/csweet/artifact-media");
        var paths = new FirecrackerHelperPaths(
            dataRoot,
            Path.Combine(dataRoot, "instances"),
            Path.Combine(dataRoot, "jailer"),
            artifactRoot,
            Path.Combine(packageRoot, "firecracker"),
            Path.Combine(packageRoot, "jailer"),
            Path.Combine(packageRoot, "vmlinux"),
            Path.Combine(packageRoot, "initrd.img"),
            UnsignedEnvironment("CSWEET_FIRECRACKER_WORKLOAD_UID", 65534),
            UnsignedEnvironment("CSWEET_FIRECRACKER_WORKLOAD_GID", 65534),
            UnsignedEnvironment("CSWEET_FIRECRACKER_GUEST_VSOCK_PORT", 5000),
            ResolveParentCgroup());
        if (paths.GuestVsockPort is 0 or > 65535)
            throw new HelperProtocolException("invalid-vsock-port", "The Firecracker guest broker port is invalid.");
        return paths;
    }

    public string InstanceDirectory(Guid instanceId) => SafeChild(InstancesRoot, instanceId.ToString("N"));
    public string JailDirectory(string jailId) => SafeChild(JailerRoot, Path.Combine("firecracker", jailId, "root"));

    internal static string SafeChild(string root, string relative)
    {
        if (!Path.IsPathFullyQualified(root) || string.IsNullOrWhiteSpace(relative) ||
            Path.IsPathFullyQualified(relative) || relative.Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is "" or "." or ".." || segment.Any(char.IsControl)))
            throw new HelperProtocolException("invalid-path", "A Firecracker helper path is invalid.");
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(prefix, relative));
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            throw new HelperProtocolException("invalid-path", "A Firecracker helper path escaped its protected root.");
        return path;
    }

    private static string AbsoluteEnvironment(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        var path = string.IsNullOrWhiteSpace(value) ? fallback : value;
        if (!Path.IsPathFullyQualified(path))
            throw new HelperProtocolException("invalid-path", $"The configured {name} path must be absolute.");
        return Path.GetFullPath(path);
    }

    private static uint UnsignedEnvironment(string name, uint fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : uint.TryParse(value, out var parsed) && parsed > 0
                ? parsed
                : throw new HelperProtocolException("invalid-identity", $"The configured {name} value is invalid.");
    }

    private static string ResolveParentCgroup()
    {
        if (!OperatingSystem.IsLinux()) return "unsupported-host";
        var configured = Environment.GetEnvironmentVariable("CSWEET_FIRECRACKER_PARENT_CGROUP");
        var value = string.IsNullOrWhiteSpace(configured)
            ? File.ReadLines("/proc/self/cgroup")
                .Select(line => line.Split(':', 3))
                .Where(parts => parts.Length == 3 && parts[0] == "0" && parts[1] == string.Empty)
                .Select(parts => parts[2].Trim('/'))
                .SingleOrDefault()
            : configured.Trim('/');
        if (string.IsNullOrWhiteSpace(value) || value.Split('/')
            .Any(segment => segment is "" or "." or ".." || segment.Any(char.IsControl)))
            throw new HelperProtocolException("invalid-cgroup", "The delegated RuntimeHost cgroup path is invalid.");
        return value;
    }
}
