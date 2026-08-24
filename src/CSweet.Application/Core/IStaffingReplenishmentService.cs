using CSweet.Contracts.Core;

namespace CSweet.Application.Core;

public interface IStaffingReplenishmentService
{
    Task<StaffingReplenishmentResponse> ProposeAsync(Guid organizationId, Guid requesterInstallationId,
        StaffingReplenishmentProposalRequest request, CancellationToken cancellationToken = default);
    Task<StaffingReplenishmentReadResponse> ReadForInstallationAsync(Guid organizationId, Guid installationId,
        StaffingReplenishmentReadRequest request, CancellationToken cancellationToken = default);
    Task<StaffingReplenishmentResponse> DecideForInstallationAsync(Guid organizationId, Guid managerInstallationId,
        StaffingReplenishmentDecisionRequest request, CancellationToken cancellationToken = default);
    Task<StaffingReplenishmentResponse> DecideForUserAsync(Guid organizationId, Guid applicationUserId,
        StaffingReplenishmentDecisionRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StaffingReplenishmentResponse>> ListForDashboardAsync(Guid organizationId,
        CancellationToken cancellationToken = default);
}
