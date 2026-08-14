using CSweet.Contracts.Agents;

namespace CSweet.Application.Setup;

public interface IAgentDefinitionService
{
    Task<AgentDefinitionResponse> ImportAsync(Guid importId, InstallAgentRequest request,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentDefinitionResponse>> ListAsync(CancellationToken cancellationToken = default);
    Task<AgentDefinitionResponse?> GetAsync(Guid definitionId, CancellationToken cancellationToken = default);
    Task<AgentDefinitionResponse> RetryBuildAsync(Guid definitionId, CancellationToken cancellationToken = default);
}
