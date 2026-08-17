using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSweet.Contracts.Plugins;

public sealed record PluginManifest
{
    public string ManifestVersion { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public PluginPublisher Publisher { get; init; } = new();
    public PluginRuntime Runtime { get; init; } = new();
    public PluginProtocol Protocol { get; init; } = new();
    public IReadOnlyList<PluginCapabilityDeclaration> Provides { get; init; } = [];
    public IReadOnlyList<PluginCapabilityRequirement> Requires { get; init; } = [];
    public PluginEventDeclarations Events { get; init; } = new();
    public IReadOnlyList<PluginConfigurationField> Configuration { get; init; } = [];
    public IReadOnlyList<PluginCredentialBinding> Credentials { get; init; } = [];
    public IReadOnlyList<PluginConnectionDeclaration> Connections { get; init; } = [];
    public PluginSetupManifest? Setup { get; init; }
    public PluginWebAccess WebAccess { get; init; } = new();
    public IReadOnlyList<PluginUiContribution> Ui { get; init; } = [];
    public PluginCatalogMetadata Catalog { get; init; } = new();
}

public sealed record PluginCatalogMetadata
{
    public string? Summary { get; init; }
    public string? Category { get; init; }
    public PluginCatalogRole? Role { get; init; }
    public PluginCatalogLicense? License { get; init; }
    public IReadOnlyList<string> IconUrls { get; init; } = [];
    public IReadOnlyList<string> RoleAliases { get; init; } = [];
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public string? DocumentationUrl { get; init; }
}

public sealed record PluginCatalogRole
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

public sealed record PluginCatalogLicense
{
    public string SpdxId { get; init; } = string.Empty;
    public string? Url { get; init; }
}

public sealed record PluginPublisher
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

public sealed record PluginRuntime
{
    public string Type { get; init; } = string.Empty;
    public string? ProjectPath { get; init; }
    public string? TargetFramework { get; init; }
    public string DefaultActivationMode { get; init; } = "OnDemand";
    public bool SupportsMultipleInstallations { get; init; }
    public int MaximumConcurrentJobs { get; init; } = 1;
    public string? EnvironmentProfile { get; init; }
    public string WorkspaceAccess { get; init; } = "None";
}

public sealed record PluginProtocol
{
    public string MinimumVersion { get; init; } = string.Empty;
    public string MaximumVersion { get; init; } = string.Empty;
}

public sealed record PluginCapabilityDeclaration
{
    public const int MaximumExecutionTimeoutSeconds = 86_400;

    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public JsonElement InputSchema { get; init; }
    public JsonElement OutputSchema { get; init; }
    public int ExecutionTimeoutSeconds { get; init; } = 30;
    public string Idempotency { get; init; } = "work-item";
    public string RiskClass { get; init; } = "standard";
    public string? DescriptorHash { get; init; }
}

public sealed record PluginCapabilityRequirement
{
    public string Name { get; init; } = string.Empty;
    public string Scope { get; init; } = "organization";
    public string? Purpose { get; init; }
}

public sealed record PluginEventDeclarations
{
    public IReadOnlyList<string> Publishes { get; init; } = [];
    public IReadOnlyList<string> Subscribes { get; init; } = [];
}

public sealed record PluginConfigurationField
{
    public string Key { get; init; } = string.Empty;
    public string Type { get; init; } = "string";
    public string Label { get; init; } = string.Empty;
    public bool Required { get; init; }
    public bool Secret { get; init; }
    public string? Description { get; init; }
    public JsonElement? DefaultValue { get; init; }
    public IReadOnlyList<PluginConfigurationOption>? Options { get; init; }
    public decimal? Minimum { get; init; }
    public decimal? Maximum { get; init; }
    public decimal? Step { get; init; }
    public string? DependsOnFieldKey { get; init; }
}

public sealed record PluginConfigurationOption(string Value, string Label);

public sealed record PluginCredentialBinding
{
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];
}

public sealed record PluginConnectionDeclaration
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = "oauth2";
    public string ProviderProfile { get; init; } = string.Empty;
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];
    public IReadOnlyList<PluginConnectionScopeSet> ScopeSets { get; init; } = [];
    public IReadOnlyList<string> SecretResponseFields { get; init; } = [];
}

public sealed record PluginConnectionScopeSet
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Purpose { get; init; } = string.Empty;
    public bool Required { get; init; }
    public IReadOnlyList<string> Scopes { get; init; } = [];
}

public sealed record PluginSetupManifest
{
    public bool Required { get; init; } = true;
    public string EntryFlow { get; init; } = string.Empty;
    public IReadOnlyList<PluginSetupFlow> Flows { get; init; } = [];
}

public sealed record PluginSetupFlow
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public IReadOnlyList<PluginSetupStep> Steps { get; init; } = [];
}

public sealed record PluginSetupStep
{
    public string Id { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Connection { get; init; }
    public string? ScopeSet { get; init; }
    public string? Capability { get; init; }
    public IReadOnlyList<string> ConfigurationKeys { get; init; } = [];
}

public sealed record PluginWebAccess
{
    [JsonConverter(typeof(JsonStringEnumConverter<PluginWebAccessMode>))]
    public PluginWebAccessMode Mode { get; init; } = PluginWebAccessMode.None;
    public IReadOnlyList<PluginWebAccessRule> Rules { get; init; } = [];
    public string? Purpose { get; init; }
}

public enum PluginWebAccessMode
{
    None,
    Allowlist,
    AllPublic
}

public sealed record PluginWebAccessRule
{
    public string Scheme { get; init; } = "https";
    public string Host { get; init; } = string.Empty;
    public int? Port { get; init; }
    public string PathPrefix { get; init; } = "/";
    public IReadOnlyList<string> Methods { get; init; } = ["GET"];
    public string Protocol { get; init; } = "http";
    public string Purpose { get; init; } = string.Empty;
    public string? Credential { get; init; }
    public string? Connection { get; init; }
    public bool Bootstrap { get; init; }
}

public sealed record PluginUiContribution
{
    public string Kind { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Capability { get; init; }
    public string? Flow { get; init; }
}

public sealed record PlatformWebFetchRequest(
    string Url,
    string Method = "GET",
    IReadOnlyDictionary<string, string>? Headers = null,
    string? Credential = null,
    byte[]? Body = null,
    string? ContentType = null,
    string? Connection = null,
    string? BoundResourceId = null);

public sealed record PlatformWebFetchResponse(
    int StatusCode,
    string FinalUrl,
    string ContentType,
    byte[] Body,
    bool Truncated);

public sealed record PlatformWebSocketRequest(
    string Operation,
    string? Url = null,
    string? ConnectionId = null,
    byte[]? Payload = null,
    string MessageType = "text",
    string? Credential = null);

public sealed record PlatformWebSocketResponse(
    string ConnectionId,
    byte[]? Payload = null,
    string MessageType = "text",
    bool EndOfMessage = true,
    int? CloseStatus = null,
    string? CloseDescription = null);
