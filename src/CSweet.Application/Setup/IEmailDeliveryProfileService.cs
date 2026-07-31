using CSweet.Contracts.Setup;

namespace CSweet.Application.Setup;

public interface IEmailDeliveryProfileService
{
    Task<IReadOnlyList<EmailDeliveryProfileResponse>> ListAsync(CancellationToken cancellationToken = default);
    Task<EmailDeliveryProfileActionResponse> CreateAsync(SaveEmailDeliveryProfileRequest request, CancellationToken cancellationToken = default);
    Task<EmailDeliveryProfileActionResponse> UpdateAsync(Guid id, SaveEmailDeliveryProfileRequest request, CancellationToken cancellationToken = default);
    Task<EmailDeliveryProfileActionResponse> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<EmailDeliveryProfileActionResponse> TestAsync(Guid id, Guid applicationUserId, CancellationToken cancellationToken = default);
    Task<EmailDeliveryProfileActionResponse> SetDefaultAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> HasReadyDefaultAsync(CancellationToken cancellationToken = default);
}
