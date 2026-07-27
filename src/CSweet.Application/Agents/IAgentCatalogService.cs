using CSweet.Agent.SDK;

namespace CSweet.Application.Agents;

public interface IAgentCatalogService
{
    Task<AvailableAgentSearchResult> GetAvailableAgentsAsync(
        Guid? organizationId,
        AvailableAgentSearchQuery query,
        CancellationToken cancellationToken = default);

    Task<AvailableAgent?> ResolveAsync(
        Guid? organizationId,
        string agentReference,
        CancellationToken cancellationToken = default);
}

public interface IAgentCatalogProvider
{
    AgentCatalogSource Source { get; }

    Task<AgentCatalogProviderResult> SearchAsync(
        Guid? organizationId,
        AvailableAgentSearchQuery query,
        CancellationToken cancellationToken = default);

    Task<AvailableAgent?> ResolveAsync(
        Guid? organizationId,
        string agentReference,
        CancellationToken cancellationToken = default);
}

public sealed record AgentCatalogProviderResult(
    IReadOnlyList<AvailableAgent> Agents,
    AgentCatalogSourceHealth Health);

public interface ILocalAgentSourceArchiveService
{
    Task<LocalAgentSourceArchive> CreateArchiveAsync(
        string agentReference,
        CancellationToken cancellationToken = default);
}

public sealed record LocalAgentSourceArchive(
    string FileName,
    byte[] Content,
    string SourceDigest);
