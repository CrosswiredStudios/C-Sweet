using CSweet.Contracts.Core;

namespace CSweet.Application.Core;

public interface IAgentHireOperationService
{
    Task<AgentHireOperationResponse?> StartAsync(
        Guid organizationId,
        Guid workflowId,
        Guid applicationUserId,
        ConfirmHiringWorkflowRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentHireOperationResponse>> ListForUserAsync(
        Guid applicationUserId,
        CancellationToken cancellationToken = default);

    Task<AgentHireOperationResponse?> GetForUserAsync(
        Guid operationId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default);

    Task<AgentHireOperationResponse?> RetryAsync(
        Guid operationId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default);

    Task<AgentHireOperationResponse?> DismissAsync(
        Guid operationId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default);

    Task<bool> ProcessNextAsync(string leaseOwner, CancellationToken cancellationToken = default);
}
