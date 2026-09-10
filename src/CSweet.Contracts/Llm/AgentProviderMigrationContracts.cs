namespace CSweet.Contracts.Llm;

public sealed record AgentProviderMigrationCandidate(Guid DefinitionId, string AgentId, string Name, long Revision, int InstanceCount);
public sealed record AgentProviderMigrationSelection(Guid DefinitionId, long ExpectedRevision);
public sealed record MigrateAgentProvidersRequest(Guid ProviderId, string Model,
    IReadOnlyList<AgentProviderMigrationSelection> Agents, bool IncludeInstances);
public sealed record MigrateAgentProvidersResponse(int AgentCount, int InstanceCount);
