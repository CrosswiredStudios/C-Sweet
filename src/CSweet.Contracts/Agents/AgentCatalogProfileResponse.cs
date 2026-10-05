using CSweet.Contracts.Plugins;

namespace CSweet.Contracts.Agents;

/// <summary>Manifest declarations for browsing; these are requests, not approved grants.</summary>
public sealed record AgentCatalogProfileResponse(
    string AgentReference,
    string Version,
    IReadOnlyList<PluginCapabilityDeclaration> Capabilities,
    IReadOnlyList<PluginCapabilityRequirement> RequestedGrants,
    IReadOnlyList<string> Subscriptions,
    IReadOnlyList<string> Publications,
    PluginWebAccess WebAccess,
    IReadOnlyList<PluginCredentialBinding> Credentials,
    IReadOnlyList<PluginConnectionDeclaration> Connections);
