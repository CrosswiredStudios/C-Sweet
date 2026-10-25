namespace CSweet.Contracts.Plugins;

/// <summary>Read-only admission status. Never changes runtime security policy.</summary>
public sealed record PluginInstallationPolicyResponse(bool ImportedPluginsEnabled);
