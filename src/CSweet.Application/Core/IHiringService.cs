using CSweet.Contracts.Core;

namespace CSweet.Application.Core;

public interface IHiringService
{
    Task<HiringRecommendationResponse> UpsertRecommendationAsync(Guid organizationId, Guid requestingInstallationId,
        UpsertHiringRecommendationRequest request, CancellationToken cancellationToken = default);
    Task<HiringRecommendationResponse> ResolveRecommendationAsync(Guid organizationId, Guid requestingInstallationId,
        ResolveHiringRecommendationRequest request, CancellationToken cancellationToken = default);
    Task<HiringRecommendationResponse> WithdrawRecommendationAsync(Guid organizationId, Guid requestingInstallationId,
        WithdrawHiringRecommendationRequest request, CancellationToken cancellationToken = default);
    Task<HiringWorkflowResponse> StageWorkflowAsync(Guid organizationId, Guid requestingInstallationId,
        StageHiringWorkflowRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HiringRecommendationResponse>> ListRecommendationsAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HiringRecommendationResponse>> ListRecommendationsForInstallationAsync(Guid organizationId,
        Guid requestingInstallationId, CancellationToken cancellationToken = default);
    Task<HiringDashboardResponse> GetDashboardAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task<HiringWorkflowResponse?> ConfirmWorkflowAsync(Guid organizationId, Guid workflowId, Guid applicationUserId,
        ConfirmHiringWorkflowRequest request, CancellationToken cancellationToken = default);
    Task<HiringWorkflowResponse?> DecideWorkflowAsync(Guid organizationId, Guid workflowId, Guid applicationUserId,
        DecideHiringWorkflowRequest request, CancellationToken cancellationToken = default);
    Task<HiringWorkflowResponse?> CancelMarketplacePreviewAsync(Guid organizationId, Guid workflowId,
        Guid applicationUserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, HiringWorkflowApprovalResponse>> ListApprovalCardsAsync(
        Guid organizationId, Guid? conversationId = null, CancellationToken cancellationToken = default);
    Task<MarketplaceHirePreviewResponse> PreviewMarketplaceHireAsync(Guid organizationId, Guid applicationUserId,
        PreviewMarketplaceHireRequest request, CancellationToken cancellationToken = default);
}
