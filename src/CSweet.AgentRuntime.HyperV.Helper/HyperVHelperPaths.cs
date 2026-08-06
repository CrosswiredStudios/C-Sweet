namespace CSweet.AgentRuntime.HyperV.Helper;

internal sealed record HyperVHelperPaths(
    string DataRoot,
    string InstancesRoot,
    string VmConfigurationRoot,
    string ArtifactMediaRoot)
{
    public static HyperVHelperPaths Resolve()
    {
        var configured = Environment.GetEnvironmentVariable("CSWEET_HYPERV_DATA_ROOT");
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(programData, "CSweet", "RuntimeHost", "HyperV")
            : configured;
        if (!Path.IsPathFullyQualified(root))
            throw new HelperProtocolException("invalid-data-root", "The Hyper-V data root must be an absolute path.");
        root = Path.GetFullPath(root);
        var instances = Path.Combine(root, "instances");
        var configuration = Path.Combine(root, "vm-config");
        var configuredMedia = Environment.GetEnvironmentVariable("CSWEET_ARTIFACT_MEDIA_ROOT");
        var media = string.IsNullOrWhiteSpace(configuredMedia)
            ? Path.Combine(programData, "CSweet", "AgentRuntime", "artifact-media")
            : configuredMedia;
        if (!Path.IsPathFullyQualified(media))
            throw new HelperProtocolException("invalid-artifact-root", "The artifact media root must be an absolute path.");
        return new HyperVHelperPaths(root, instances, configuration, Path.GetFullPath(media));
    }

    public string InstanceDirectory(Guid instanceId)
    {
        var path = Path.GetFullPath(Path.Combine(InstancesRoot, instanceId.ToString("N")));
        var prefix = Path.GetFullPath(InstancesRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new HelperProtocolException("invalid-instance", "The instance path escaped the configured data root.");
        return path;
    }
}
