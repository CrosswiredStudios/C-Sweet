using CSweet.Contracts.Core;

namespace CSweet.Application.Core;

public interface IApprovalDashboardService
{
    Task<ApprovalDashboardResponse> GetAsync(
        Guid organizationId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default);
}
