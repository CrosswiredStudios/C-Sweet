using CSweet.Contracts.Setup;

namespace CSweet.Application.Setup;

public interface IAgentIsolationOnboardingService
{
    Task<AgentIsolationOnboardingResponse> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<AgentIsolationOnboardingActionResponse> EnableHostHypervisorAsync(
        CancellationToken cancellationToken = default);

    Task<AgentIsolationOnboardingActionResponse> InstallWindowsRuntimeHostAsync(
        CancellationToken cancellationToken = default);
}
