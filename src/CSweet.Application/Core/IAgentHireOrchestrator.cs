using CSweet.Contracts.Core;

namespace CSweet.Application.Core;

/// <summary>
/// Canonical organization-scoped flow for previewing and confirming agent hires,
/// independent of the catalog source used to materialize the installation.
/// </summary>
public interface IAgentHireOrchestrator
{
    Task<MarketplaceHirePreviewResponse> PreviewAsync(
        Guid organizationId,
        Guid applicationUserId,
        PreviewMarketplaceHireRequest request,
        CancellationToken cancellationToken = default);

    Task<AgentHireOperationResponse?> ConfirmAsync(
        Guid organizationId,
        Guid workflowId,
        Guid applicationUserId,
        ConfirmHiringWorkflowRequest request,
        CancellationToken cancellationToken = default);
}
