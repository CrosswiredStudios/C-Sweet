namespace CSweet.Domain.Setup;

/// <summary>Validated global defaults for an agent definition.</summary>
public sealed class AgentDefinitionConfiguration
{
    public Guid Id { get; set; }
    public Guid AgentDefinitionId { get; set; }
    public string SchemaVersion { get; set; } = "1";
    public string SettingsJson { get; set; } = "{}";
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public AgentDefinition? AgentDefinition { get; set; }
}
