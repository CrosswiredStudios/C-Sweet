using CSweet.Contracts.BusinessOnboarding;

namespace CSweet.Application.BusinessOnboarding;

public interface IBusinessOnboardingOperationService
{
    Task<BusinessOnboardingOperationResponse> StartAsync(
        StartBusinessOnboardingRequest request,
        Guid applicationUserId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BusinessOnboardingOperationResponse>> ListForUserAsync(
        Guid applicationUserId,
        CancellationToken cancellationToken = default);

    Task<BusinessOnboardingOperationResponse?> GetForUserAsync(
        Guid operationId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default);

    Task<BusinessOnboardingOperationResponse?> RetryAsync(
        Guid operationId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default);

    Task<BusinessOnboardingOperationResponse?> DismissAsync(
        Guid operationId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default);

    Task<bool> ProcessNextAsync(string leaseOwner, CancellationToken cancellationToken = default);
}
