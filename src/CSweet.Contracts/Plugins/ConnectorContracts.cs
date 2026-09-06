using System.Text.Json;

namespace CSweet.Contracts.Plugins;

/// <summary>Requests the host's fixed, protected-conversation-only setup assistance profile.</summary>
public sealed record PluginSetupAssistance
{
    public string Profile { get; init; } = "conversation.v1";
}

/// <summary>An exact package requirement, resolved by the host to an approved immutable installation.</summary>
public sealed record PluginDependencyDeclaration
{
    public string Id { get; init; } = string.Empty;
    public string PluginId { get; init; } = string.Empty;
    public string PublisherId { get; init; } = string.Empty;
    public string MinimumVersion { get; init; } = string.Empty;
    public string MaximumVersionExclusive { get; init; } = string.Empty;
}

/// <summary>Public provider information. Client credentials are never package metadata.</summary>
public sealed record OAuthProviderMetadata
{
    public string DisplayName { get; init; } = string.Empty;
    public string AuthorizationEndpoint { get; init; } = string.Empty;
    public string TokenEndpoint { get; init; } = string.Empty;
    public string RevocationEndpoint { get; init; } = string.Empty;
    public string ClientAuthentication { get; init; } = "client_secret_post";
    public IReadOnlyDictionary<string, string> AuthorizationParameters { get; init; } = new Dictionary<string, string>();
}

/// <summary>A closed request mapping materialized by the host, never an executable template.</summary>
public sealed record ConnectorHttpOperation
{
    public string Connection { get; init; } = string.Empty;
    public IReadOnlyList<string> ScopeSets { get; init; } = [];
    public string Method { get; init; } = "GET";
    public string Endpoint { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> QueryConstants { get; init; } = new Dictionary<string, string>();
    /// <summary>Query parameter name to an RFC 6901 pointer into validated input.</summary>
    public IReadOnlyDictionary<string, string> QueryInputs { get; init; } = new Dictionary<string, string>();
    /// <summary>Body field pointer to an RFC 6901 pointer into validated input.</summary>
    public IReadOnlyDictionary<string, string> BodyInputs { get; init; } = new Dictionary<string, string>();
    public JsonElement? BodyConstants { get; init; }
    /// <summary>Optional query field whose value is supplied exclusively from the confirmed connection.</summary>
    public string? BoundResourceQuery { get; init; }
    public IReadOnlyList<ConnectorResourceCheck> ResourceChecks { get; init; } = [];
    public string? MediaInput { get; init; }
    public bool Bootstrap { get; init; }
    public IReadOnlyList<string> SecretResponseFields { get; init; } = [];
}

/// <summary>Provider read which proves that the input resource belongs to the connected account.</summary>
public sealed record ConnectorResourceCheck
{
    public string Endpoint { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> QueryConstants { get; init; } = new Dictionary<string, string>();
    public string ResourceQuery { get; init; } = "id";
    public string InputPointer { get; init; } = string.Empty;
    public string OwnerPointer { get; init; } = string.Empty;
}
