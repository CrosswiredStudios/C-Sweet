namespace CSweet.Domain.Setup;

public sealed class AgentPackageVersion
{
    public Guid Id { get; set; }
    public Guid PackageSourceId { get; set; }
    public string CommitSha { get; set; } = string.Empty;
    public string ManifestDigest { get; set; } = string.Empty;
    public string CapabilityDescriptorsDigest { get; set; } = string.Empty;
    public string ManifestJson { get; set; } = string.Empty;
    public PluginKind PluginKind { get; set; } = PluginKind.Agent;
    public string ManifestFileName { get; set; } = "csweet-plugin.json";
    public string AgentId { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string PublisherId { get; set; } = string.Empty;
    public string PublisherName { get; set; } = string.Empty;
    public string RuntimeType { get; set; } = string.Empty;
    public string? ProjectPath { get; set; }
    public string? TargetFramework { get; set; }
    public string? DefaultActivationMode { get; set; }
    public string WarningsJson { get; set; } = "[]";
    public AgentPackageVersionStatus Status { get; set; } = AgentPackageVersionStatus.Previewed;
    public string? PackageDigest { get; set; }
    public string? PackagePath { get; set; }
    public string? ArtifactSignature { get; set; }
    public string ArtifactFormatVersion { get; set; } = "1.0";
    public string ArtifactOperatingSystem { get; set; } = "linux";
    public string ArtifactArchitecture { get; set; } = "x64";
    public DateTimeOffset? BuiltAt { get; set; }
    public DateTimeOffset ImportedAt { get; set; }

    public AgentPackageSource? PackageSource { get; set; }
    public ICollection<AgentBuildJob> BuildJobs { get; set; } = [];
}
