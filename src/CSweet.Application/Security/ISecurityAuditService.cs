using CSweet.Contracts.Security;

namespace CSweet.Application.Security;

public interface ISecurityAuditService
{
    Task<SecurityEventPageResponse> BrowseAsync(
        Guid organizationId,
        SecurityEventQuery query,
        CancellationToken cancellationToken = default);

    Task<SecurityEventDetailResponse?> GetAsync(
        Guid organizationId,
        Guid eventId,
        CancellationToken cancellationToken = default,
        bool groupModelResponse = false,
        Guid? employeeId = null);
}
