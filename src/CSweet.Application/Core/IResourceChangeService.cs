using CSweet.Contracts.Core;

namespace CSweet.Application.Core;

public interface IResourceChangeService
{
    Task<ResourceChangeRequestResponse> ProposeAsync(
        Guid organizationId,
        Guid requesterInstallationId,
        ResourceChangeProposalRequest request,
        CancellationToken cancellationToken = default);

    Task<ResourceChangeReadResponse> ReadForInstallationAsync(
        Guid organizationId,
        Guid installationId,
        ResourceChangeReadRequest request,
        CancellationToken cancellationToken = default);

    Task<ResourceChangeRequestResponse> DecideForInstallationAsync(
        Guid organizationId,
        Guid managerInstallationId,
        ResourceChangeDecisionRequest request,
        CancellationToken cancellationToken = default);

    Task<ResourceChangeRequestResponse> DecideForUserAsync(
        Guid organizationId,
        Guid applicationUserId,
        ResourceChangeDecisionRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ResourceChangeRequestResponse>> ListForDashboardAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default);
}
