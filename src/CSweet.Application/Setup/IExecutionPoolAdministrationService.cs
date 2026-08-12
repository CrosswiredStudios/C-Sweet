using CSweet.Contracts.Setup;

namespace CSweet.Application.Setup;

public interface IExecutionPoolAdministrationService
{
    Task<ExecutionFleetMutationResponse> CreatePoolAsync(
        CreateExecutionPoolRequest request,
        CancellationToken cancellationToken = default);

    Task<ExecutionFleetMutationResponse> UpdatePoolAsync(
        Guid poolId,
        UpdateExecutionPoolRequest request,
        CancellationToken cancellationToken = default);

    Task<ExecutionFleetMutationResponse> DeletePoolAsync(
        Guid poolId,
        CancellationToken cancellationToken = default);

    Task<ExecutionFleetMutationResponse> SetInstallationPoolAsync(
        Guid installationId,
        UpdateAgentExecutionPoolRequest request,
        CancellationToken cancellationToken = default);
}
