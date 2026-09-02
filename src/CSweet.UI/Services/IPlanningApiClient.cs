using CSweet.Contracts.Planning;

namespace CSweet.UI.Services;

public interface IPlanningApiClient
{
    Task<PlanningRunResponse> StartRunAsync(StartPlanningRunRequest request, CancellationToken cancellationToken = default);
    Task<PlanningStatusResponse> GetStatusAsync(Guid organizationId, string workflowKey, CancellationToken cancellationToken = default);
    Task<PlanningActionResponse> RunNextTaskAsync(Guid organizationId, string workflowKey, CancellationToken cancellationToken = default);
    Task<PlanningActionResponse> CancelRunAsync(Guid organizationId, string workflowKey, CancellationToken cancellationToken = default);
    Task<PlanningActionResponse> ResetRunAsync(Guid organizationId, string workflowKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlanningWorkflowResponse>> ListWorkflowsAsync(CancellationToken cancellationToken = default);
}
