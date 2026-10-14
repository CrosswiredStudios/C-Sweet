using CSweet.Contracts.Agents;

namespace CSweet.Application.PersonalAssistant;

public interface IBundledAssistantInstallationService
{
    Task<AgentInstallationResponse> InstallAsync(Guid packageVersionId, Guid userId, InstallAgentRequest request, CancellationToken token);
}
