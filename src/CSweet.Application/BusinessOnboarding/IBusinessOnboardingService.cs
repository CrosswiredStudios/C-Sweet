using CSweet.Contracts.BusinessOnboarding;

namespace CSweet.Application.BusinessOnboarding;

public interface IBusinessOnboardingService
{
    Task<BusinessOnboardingActionResponse> CompleteAsync(
        CompleteBusinessOnboardingRequest request,
        CancellationToken cancellationToken = default,
        Guid? applicationUserId = null);

    Task<ChiefSetupActionResponse> AssignChiefAsync(
        Guid organizationId,
        CompleteChiefSetupRequest request,
        CancellationToken cancellationToken = default);

    Task<ChiefSetupActionResponse> AssignExistingChiefAsync(
        Guid organizationId,
        Guid organizationUserId,
        CancellationToken cancellationToken = default);
}
