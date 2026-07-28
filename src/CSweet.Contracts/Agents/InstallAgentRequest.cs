using System.Text.Json;

namespace CSweet.Contracts.Agents;

public sealed record InstallAgentRequest(
    string BusinessId,
    string ActivationMode,
    int TickFrequencySeconds,
    string OverlapPolicy,
    IReadOnlyList<string> GrantedCapabilities,
    IReadOnlyList<string> GrantedSubscriptions,
    IReadOnlyList<string> GrantedPublications,
    IReadOnlyList<string> GrantedPermissions,
    IReadOnlyList<string> GrantedNetworkAccess,
    int MaxRuntimeSeconds,
    int MemoryMb,
    int CpuPercent)
{
    public string PluginScope { get; init; } = "Organization";
    public IReadOnlyList<string> GrantedRequestedCapabilities { get; init; } = [];
    public IReadOnlyDictionary<string, Guid> CapabilityBindings { get; init; } =
        new Dictionary<string, Guid>(StringComparer.Ordinal);
    public string ConfigurationSchemaVersion { get; init; } = "1";
    public IReadOnlyDictionary<string, JsonElement> ConfigurationSettings { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    public bool AllPublicWebAccessAcknowledged { get; init; }
}
