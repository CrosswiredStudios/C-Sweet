using CSweet.Agent.SDK;

namespace CSweet.Application.Agents;

public interface IAgentCatalogService
{
    /// <summary>Manual-only pilots remain discoverable, but cannot be selected or executed by automatic hiring.</summary>
    bool IsAutomaticSelectionAllowed(AvailableAgent agent) => true;

    Task<AvailableAgentSearchResult> GetAvailableAgentsAsync(
        Guid? organizationId,
        AvailableAgentSearchQuery query,
        CancellationToken cancellationToken = default);

    Task<AvailableAgentSearchResult> GetHiringCandidatesAsync(Guid organizationId, AvailableAgentSearchQuery query,
        CancellationToken cancellationToken) => GetAvailableAgentsAsync(organizationId, query, cancellationToken);

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

    Task<AgentCatalogProviderResult> SearchForHiringAsync(Guid organizationId, AvailableAgentSearchQuery query,
        CancellationToken cancellationToken) => SearchAsync(organizationId, query, cancellationToken);

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
