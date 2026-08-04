using CSweet.Application.SourceControl;

namespace CSweet.Infrastructure.SourceControl;

/// <summary>No local/provider fallback is permitted when the trusted source-control host is absent.</summary>
public sealed class UnavailableTrustedSourceControlHostClient : ITrustedSourceControlHostClient
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

    public Task<TrustedMergeResult> MergeAsync(
        TrustedMergeRequest request,
        CancellationToken cancellationToken = default) =>
        throw Unavailable();

    public Task<TrustedWorkspaceSnapshot> PrepareWorkspaceAsync(
        TrustedWorkspaceSnapshotRequest request,
        CancellationToken cancellationToken = default) =>
        throw Unavailable();

    public Task<IReadOnlyList<TrustedRepositoryDescriptor>> ListRepositoriesAsync(
        long installationId,
        CancellationToken cancellationToken = default) =>
        throw Unavailable();

    private static InvalidOperationException Unavailable() => new(
        "The trusted source-control host is not configured; provider access is blocked without exposing credentials.");
}
