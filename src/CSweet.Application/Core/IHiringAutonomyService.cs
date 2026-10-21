using CSweet.Agent.SDK;
using CSweet.Contracts.Core;
using HiringRecommendationResponse = CSweet.Contracts.Core.HiringRecommendationResponse;
namespace CSweet.Application.Core;
public interface IHiringAutonomyService
{
    Task<HiringPolicyResponse> ReadPolicyAsync(Guid organizationId, Guid installationId, CancellationToken token = default);
    Task<HiringPolicyResponse> UpdatePolicyAsync(Guid organizationId, Guid installationId, Guid applicationUserId, HiringPolicySettings settings, long expectedRevision, string rationale, Guid? decisionId = null, CancellationToken token = default);
    Task<HiringPolicyResponse> CaptureDecisionAsync(Guid organizationId, Guid installationId, CaptureHiringPolicyDecisionRequest request, CancellationToken token = default);
    Task<HiringCandidateSelectionResponse> SelectCandidateAsync(Guid organizationId, Guid installationId, Guid recommendationId, CancellationToken token = default);
    Task<DelegatedHireResponse> SubmitDelegatedAsync(Guid organizationId, Guid installationId, Guid recommendationId, CancellationToken token = default);
    Task ApplyToPlanAsync(Guid organizationId, Guid installationId, Guid requestId, Guid applicationUserId, CancellationToken token = default);
    Task<HiringRecommendationResponse?> ChooseRoleManuallyAsync(Guid organizationId, Guid recommendationId, Guid applicationUserId, CancellationToken token = default);
}
