using CSweet.Contracts.SourceControl;

namespace CSweet.Application.SourceControl;

public interface ISourceControlOnboardingService
{
    Task<SourceControlDashboardResponse> GetDashboardAsync(
        Guid organizationId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default);

    Task<StartSourceControlOnboardingResponse> StartAsync(
        Guid organizationId,
        Guid applicationUserId,
        StartSourceControlOnboardingRequest request,
        CancellationToken cancellationToken = default);

    Task<CompleteGitHubAppInstallationResponse> CompleteGitHubInstallationAsync(
        Guid organizationId,
        Guid applicationUserId,
        Guid sessionId,
        CompleteGitHubAppInstallationRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AvailableSourceControlRepository>> ListAvailableRepositoriesAsync(
        Guid organizationId,
        Guid applicationUserId,
        Guid connectionId,
        bool templates,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SourceControlRepositorySummary>> SelectExistingRepositoriesAsync(
        Guid organizationId,
        Guid applicationUserId,
        Guid connectionId,
        SelectExistingCodeProjectsRequest request,
        CancellationToken cancellationToken = default);

    Task<ManagedCodeProjectPolicyResponse> ConfigureManagedRepositoriesAsync(
        Guid organizationId,
        Guid applicationUserId,
        Guid connectionId,
        ConfigureManagedCodeProjectsRequest request,
        CancellationToken cancellationToken = default);
}
