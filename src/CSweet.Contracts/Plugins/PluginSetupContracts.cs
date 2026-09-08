using System.Text.Json;

namespace CSweet.Contracts.Plugins;

public sealed record PluginSetupResponse(
    Guid InstallationId,
    string State,
    string FlowId,
    string? CurrentStepId,
    IReadOnlyList<string> CompletedStepIds,
    PluginSetupFlow Flow,
    IReadOnlyList<PluginConnectionResponse> Connections)
{
    public IReadOnlyList<PluginConnectionDeclaration> ConnectionDeclarations { get; init; } = [];
    public IReadOnlyList<PluginConfigurationField> ConfigurationFields { get; init; } = [];
    public IReadOnlyDictionary<string, JsonElement> Values { get; init; } = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    public IReadOnlyList<PluginConnectionCleanupResponse> Cleanup { get; init; } = [];
}

public sealed record PluginConnectionCleanupResponse(string ConnectionId, string Status, bool RevocationConfirmed);
public sealed record PluginDisconnectImpactResponse(IReadOnlyList<string> AffectedAgents);

public sealed record PluginConnectionResponse(
    Guid Id,
    string DeclarationId,
    string ProviderProfile,
    string Status,
    IReadOnlyList<string> GrantedScopes,
    string? ExternalAccountId,
    string? ExternalAccountName,
    string? BoundResourceId);

public sealed record CompletePluginSetupStepRequest(
    IReadOnlyDictionary<string, JsonElement> Values,
    string IdempotencyKey);

public sealed record BeginPluginAuthorizationRequest(string ScopeSetId);
public sealed record BindConnectorRequest(Guid ConnectorInstallationId);
public sealed record PluginDependencySetupResponse(
    string Id, string PluginId, IReadOnlyList<string> Capabilities,
    Guid? SelectedInstallationId, IReadOnlyList<PluginConnectorChoice> Choices);
public sealed record PluginConnectorChoice(
    Guid InstallationId, string Name, string State, bool CanSelect,
    string? UnavailableReason, IReadOnlyList<PluginConnectedAccount> Accounts);
public sealed record PluginConnectedAccount(string Id, string Name);
public sealed record ApproveConnectorProfileRequest(string PackageDigest, string ProfileId);
public sealed record BeginPluginAuthorizationResponse(string AuthorizationUrl, DateTimeOffset ExpiresAt);
public sealed record PluginAuthorizationCompletion(Guid OrganizationId, Guid InstallationId);
public sealed record CompletePluginSetupResponse(bool Ready, string Message, Guid? ConversationId = null);
public sealed record PluginBootstrapCallbackResponse(JsonElement Value);

public sealed record PluginProviderProfileResponse(
    string Id,
    string DisplayName,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string? RevocationEndpoint,
    string ClientId,
    bool HasClientSecret,
    bool IsEnabled,
    bool ManagedByDeployment,
    DateTimeOffset? UpdatedAt);

public sealed record UpsertPluginProviderProfileRequest(
    string DisplayName,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string? RevocationEndpoint,
    string ClientId,
    string? ClientSecret,
    bool IsEnabled = true);
