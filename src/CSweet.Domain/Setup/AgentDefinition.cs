namespace CSweet.Domain.Setup;

/// <summary>
/// A globally installed agent family. Definitions are control-plane records and never represent
/// a running or business-scoped agent instance.
/// </summary>
public sealed class AgentDefinition
{
    public Guid Id { get; set; }
    public Guid PackageSourceId { get; set; }
    public string AgentId { get; set; } = string.Empty;
    public Guid PackageVersionId { get; set; }
    public AgentDefinitionStatus Status { get; set; } = AgentDefinitionStatus.Building;
    public bool IsAvailableForHire { get; set; }

    public ActivationMode DefaultActivationMode { get; set; } = ActivationMode.OnDemand;
    public int DefaultTickFrequencySeconds { get; set; }
    public OverlapPolicy DefaultOverlapPolicy { get; set; } = OverlapPolicy.Skip;
    public int DefaultMaxRuntimeSeconds { get; set; }
    public int DefaultMemoryMb { get; set; }
    public int DefaultCpuPercent { get; set; }
    public string DefaultProvidedCapabilitiesJson { get; set; } = "[]";
    public string DefaultRequiredCapabilitiesJson { get; set; } = "[]";
    public string DefaultEventSubscriptionsJson { get; set; } = "[]";
    public string DefaultNetworkAccessJson { get; set; } = "[]";
    public string DefaultCapabilityBindingsJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public AgentPackageSource? PackageSource { get; set; }
    public AgentPackageVersion? PackageVersion { get; set; }
    public AgentDefinitionConfiguration? Configuration { get; set; }
    public ICollection<AgentInstallation> Installations { get; set; } = [];
}

public enum AgentDefinitionStatus
{
    Building,
    NeedsConfiguration,
    Available,
    BuildFailed,
    Disabled
}
