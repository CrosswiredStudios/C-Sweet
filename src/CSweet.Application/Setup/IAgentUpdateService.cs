using CSweet.Contracts.Agents;

namespace CSweet.Application.Setup;

public interface IAgentUpdateService
{
    Task<IReadOnlyList<AgentUpdateAvailabilityResponse>> CheckAsync(
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentDefinitionUpdateAvailabilityResponse>> CheckDefinitionsAsync(
        CancellationToken cancellationToken = default);
}
