using System.Text.Json;

namespace CSweet.Contracts.Agents;

public sealed record AgentConfigurationSchemaResponse(
    string AgentId,
    string AgentVersion,
    string SchemaVersion,
    IReadOnlyList<AgentConfigurationField> Fields,
    IReadOnlyDictionary<string, JsonElement> Settings);

public sealed record AgentDefinitionResponse(
    Guid Id,
    Guid PackageVersionId,
    string AgentId,
    string AgentName,
    string AgentVersion,
    string PublisherName,
    string CommitSha,
    string Status,
    bool IsAvailableForHire,
    string DefaultActivationMode,
    int DefaultTickFrequencySeconds,
    string DefaultOverlapPolicy,
    int DefaultMaxRuntimeSeconds,
    int DefaultMemoryMb,
    int DefaultCpuPercent,
    long ConfigurationRevision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    AgentBuildSummaryResponse? Build = null);

public sealed record AgentConfigurationView(
    string AgentId,
    string AgentVersion,
    string SchemaVersion,
    IReadOnlyList<AgentConfigurationField> Fields,
    IReadOnlyDictionary<string, JsonElement> DefaultValues,
    IReadOnlyDictionary<string, JsonElement> Overrides,
    IReadOnlyDictionary<string, JsonElement> EffectiveValues,
    IReadOnlyList<string> OverriddenKeys,
    long ExpectedRevision,
    long DesiredRevision,
    long AppliedRevision,
    string SynchronizationStatus,
    string? SynchronizationError = null);

public sealed record PutAgentDefinitionConfigurationRequest(
    string SchemaVersion,
    IReadOnlyDictionary<string, JsonElement> Settings,
    long ExpectedRevision);

public sealed record PutAgentConfigurationOverridesRequest(
    IReadOnlyDictionary<string, JsonElement> Overrides,
    long ExpectedRevision);

public sealed record AgentConfigurationField(
    string Key,
    string Label,
    string Type,
    bool Required,
    string? Description = null,
    string? Placeholder = null,
    IReadOnlyList<AgentConfigurationOption>? Options = null,
    decimal? Minimum = null,
    decimal? Maximum = null,
    decimal? Step = null,
    string? DependsOnFieldKey = null);

public sealed record AgentConfigurationOption(
    string Value,
    string Label);

public sealed record UpdateAgentConfigurationRequest(
    IReadOnlyDictionary<string, JsonElement> Settings)
{
    public string? SchemaVersion { get; init; }
}

public sealed record AgentConfigurationUpdateResponse(
    bool Succeeded,
    string? Message,
    IReadOnlyDictionary<string, JsonElement> Settings);

public static class AgentConfigurationCapabilities
{
    public const string Describe = "agent.configuration.describe.v1";

    public const string Update = "agent.configuration.update.v1";
}

public static class AgentConfigurationFieldTypes
{
    public const string Text = "text";

    public const string TextArea = "textarea";

    public const string Number = "number";

    public const string Boolean = "boolean";

    public const string Select = "select";

    public const string Secret = "secret";

    public const string LlmProvider = "llmProvider";

    public const string LlmModel = "llmModel";
}
