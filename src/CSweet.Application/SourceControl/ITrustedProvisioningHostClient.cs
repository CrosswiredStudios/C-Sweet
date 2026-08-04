namespace CSweet.Application.SourceControl;

/// <summary>
/// Narrow Core-to-ProvisionerHost boundary. The host can create only private repositories from
/// approved templates and apply the fixed baseline; it exposes no general administration API.
/// </summary>
public interface ITrustedProvisioningHostClient
{
    Task<TrustedGitHubAppConfigurationStatus> GetConfigurationStatusAsync(
        CancellationToken cancellationToken = default) => Task.FromResult(
        new TrustedGitHubAppConfigurationStatus(false, null, 0, null, null, "Configuration management is unavailable."));

    Task<TrustedGitHubAppConfigurationStatus> ValidateConfigurationAsync(
        TrustedGitHubAppConfiguration configuration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException(
        "This trusted host does not support runtime configuration validation.");

    Task<TrustedGitHubAppConfigurationStatus> ActivateConfigurationAsync(
        TrustedGitHubAppConfiguration configuration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException(
        "This trusted host does not support runtime configuration activation.");

    Task<TrustedInstallationDescriptor> DescribeInstallationAsync(
        long installationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrustedRepositoryDescriptor>> ListRepositoriesAsync(
        long installationId,
        CancellationToken cancellationToken = default);

    Task<TrustedRepositoryProvisioningResult> ProvisionAsync(
        TrustedRepositoryProvisioningRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record TrustedRepositoryProvisioningRequest(
    Guid OrganizationId,
    Guid ConnectionId,
    Guid ProvisioningRequestId,
    long InstallationId,
    string OrganizationLogin,
    string RepositoryName,
    string Description,
    string TemplateOwner,
    string TemplateRepository,
    string RequiredDefaultBranch,
    string IdempotencyKey);

public sealed record TrustedRepositoryProvisioningResult(
    bool Created,
    bool Quarantined,
    long? ExternalRepositoryId,
    string? Owner,
    string? Repository,
    string? DefaultBranch,
    string? FailureCode = null,
    string? FailureMessage = null);
