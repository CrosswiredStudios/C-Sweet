using CSweet.Contracts.Agents;

namespace CSweet.Application.Agents;

public interface IAgentCatalogProfileService
{
    Task<AgentCatalogProfileResponse?> GetAsync(
        Guid? organizationId, string agentReference, CancellationToken cancellationToken = default);
}
