using CSweet.Application.SourceControl;

namespace CSweet.Infrastructure.SourceControl;

public sealed class UnavailableTrustedProvisioningHostClient : ITrustedProvisioningHostClient
{
    public Task<TrustedGitHubAppConfigurationStatus> GetConfigurationStatusAsync(
        CancellationToken cancellationToken = default) => Task.FromResult(
        new TrustedGitHubAppConfigurationStatus(false, null, 0, null, null, Unavailable().Message));

    public Task<TrustedGitHubAppConfigurationStatus> ValidateConfigurationAsync(
        TrustedGitHubAppConfiguration configuration,
        CancellationToken cancellationToken = default) => throw Unavailable();

    public Task<TrustedGitHubAppConfigurationStatus> ActivateConfigurationAsync(
        TrustedGitHubAppConfiguration configuration,
        CancellationToken cancellationToken = default) => throw Unavailable();

    public Task<TrustedInstallationDescriptor> DescribeInstallationAsync(
        long installationId,
        CancellationToken cancellationToken = default) =>
        throw Unavailable();

    public Task<TrustedRepositoryProvisioningResult> ProvisionAsync(
        TrustedRepositoryProvisioningRequest request,
        CancellationToken cancellationToken = default) =>
        throw Unavailable();

    public Task<IReadOnlyList<TrustedRepositoryDescriptor>> ListRepositoriesAsync(
        long installationId,
        CancellationToken cancellationToken = default) =>
        throw Unavailable();

    private static InvalidOperationException Unavailable() => new(
        "The trusted repository provisioner is not configured; repository creation is blocked without exposing credentials.");
}
