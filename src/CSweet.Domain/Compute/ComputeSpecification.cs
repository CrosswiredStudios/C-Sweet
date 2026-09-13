namespace CSweet.Domain.Compute;

public enum ComputeDesiredState { Running, Stopped, Destroyed }
public enum ComputeLifecycleState
{
    Requested, Authorizing, Provisioning, Bootstrapping, Ready, Busy,
    Stopping, Stopped, Destroying, Destroyed, Failed
}
public enum ComputePersistence { Ephemeral, Persistent }
public enum ComputeNetworkMode { None, Private, OutboundOnly, Inbound }
public enum ComputeStorageKind { EphemeralRoot, EphemeralAttached, PersistentVolume, Artifact }

public sealed record ComputeResources(int CpuCount, long MemoryMiB, long DiskMiB, int GpuCount = 0)
{
    public bool IsValid => CpuCount > 0 && MemoryMiB > 0 && DiskMiB > 0 && GpuCount >= 0;
    public bool Fits(ComputeResources maximum) => IsValid && maximum.IsValid &&
        CpuCount <= maximum.CpuCount && MemoryMiB <= maximum.MemoryMiB &&
        DiskMiB <= maximum.DiskMiB && GpuCount <= maximum.GpuCount;
}

/// <summary>Requested reachability, not a provider address, bridge or host firewall command.</summary>
public sealed record ComputeNetworkPolicy(
    ComputeNetworkMode Mode = ComputeNetworkMode.None,
    bool AllowOutbound = false,
    bool PublicEndpoint = false,
    IReadOnlyList<int>? PublishedPorts = null)
{
    public IReadOnlyList<int> Ports => PublishedPorts ?? [];

    public bool IsValid => Enum.IsDefined(Mode) && Ports.Count <= 64 &&
        Ports.All(x => x is > 0 and <= 65535) && Ports.Distinct().Count() == Ports.Count &&
        (Mode == ComputeNetworkMode.Inbound || (!PublicEndpoint && Ports.Count == 0)) &&
        (Mode != ComputeNetworkMode.None || !AllowOutbound) &&
        (Mode != ComputeNetworkMode.OutboundOnly || AllowOutbound) &&
        (!PublicEndpoint || Ports.Count > 0);
}

/// <summary>No host paths, provider credentials, shell bootstrap or workload-specific fields.</summary>
public sealed record ComputeSpecification(
    string OperatingSystem,
    string Architecture,
    string TemplateId,
    ComputeResources Resources,
    int LifetimeSeconds,
    ComputePersistence Persistence = ComputePersistence.Ephemeral,
    ComputeNetworkPolicy? Network = null)
{
    public ComputeNetworkPolicy NetworkPolicy => Network ?? new();

    public bool IsValid => Identifier(OperatingSystem) && Identifier(Architecture) && Identifier(TemplateId) &&
        Resources is { IsValid: true } && LifetimeSeconds > 0 &&
        Enum.IsDefined(Persistence) && NetworkPolicy.IsValid;

    public static bool Identifier(string? value) => value is { Length: > 0 and <= 64 } &&
        value[0] is >= 'a' and <= 'z' && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}

/// <summary>Operator-managed immutable image identity. Features such as Docker are optional.</summary>
public sealed record ComputeTemplate(
    string Id,
    string OperatingSystem,
    string Architecture,
    string ImageDigest,
    HashSet<string> Features,
    bool Enabled = true)
{
    public bool Matches(ComputeSpecification specification) => Enabled &&
        Id == specification.TemplateId && OperatingSystem == specification.OperatingSystem &&
        Architecture == specification.Architecture && ImageDigest is { Length: 71 } &&
        ImageDigest.StartsWith("sha256:", StringComparison.Ordinal) &&
        ImageDigest.AsSpan(7).ContainsAnyExcept("0123456789abcdef".AsSpan()) == false;
}
