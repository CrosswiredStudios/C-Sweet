using System.Text.Json.Serialization;

namespace CSweet.Contracts.Plugins;

/// <summary>Provider-neutral protocol 2.4 declarations. Never constitute an access grant.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginAccountManagement
{
    public string Layout { get; init; } = "accounts-and-resources.v1";
    public string Connection { get; init; } = string.Empty;
    public string SetupFlow { get; init; } = string.Empty;
    public string AccountLabel { get; init; } = "Account";
    public string AccountsLabel { get; init; } = "Accounts";
    public string ResourceType { get; init; } = string.Empty;
    public string ResourceLabel { get; init; } = string.Empty;
    public string ResourcesLabel { get; init; } = string.Empty;
    public string DiscoveryCapability { get; init; } = string.Empty;
    public ConnectorAccountOptions ResourceProjection { get; init; } = new();
    public string? PageTokenInput { get; init; }
    public int MaximumResources { get; init; } = 1000;
    public PluginAccountIdentity? Identity { get; init; }
    public IReadOnlyList<PluginAccountPermission> Permissions { get; init; } = [];
    public string PrivacyPolicyUrl { get; init; } = string.Empty;
    public string TermsOfServiceUrl { get; init; } = string.Empty;
    public string PolicyVersion { get; init; } = string.Empty;
    public string ConsentPurpose { get; init; } = string.Empty;
    public IReadOnlyList<PluginAccountPolicyLink> PolicyLinks { get; init; } = [];
    public PluginAccountLifecycle Lifecycle { get; init; } = new();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginAccountIdentity
{
    public string Capability { get; init; } = string.Empty;
    public string SubjectPointer { get; init; } = "/sub";
    public string? NamePointer { get; init; }
    public string? EmailPointer { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginAccountPermission
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Purpose { get; init; } = string.Empty;
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public IReadOnlyList<string> ScopeSets { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginAccountPolicyLink(string Label, string Url);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginAccountLifecycle
{
    public int AuthorizationCheckHours { get; init; } = 24;
    public int MetadataRefreshDays { get; init; } = 30;
    public int DeletionTargetHours { get; init; } = 24;
    public int DeletionMaximumDays { get; init; } = 7;
    public string RevocationScope { get; init; } = "authorization";
}
