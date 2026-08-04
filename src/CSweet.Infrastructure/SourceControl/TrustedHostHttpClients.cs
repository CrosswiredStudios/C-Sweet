using System.Net.Http.Json;
using CSweet.Application.SourceControl;
using CSweet.TrustedServices;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.SourceControl;

public sealed class TrustedSourceControlHostClient(
    HttpClient http,
    IOptions<TrustedServiceAuthenticationOptions> authentication) : ITrustedSourceControlHostClient
{
    private const long MaximumWorkspaceArchiveBytes = 600L * 1024 * 1024;
    public Task<TrustedGitHubAppConfigurationStatus> GetConfigurationStatusAsync(
        CancellationToken cancellationToken = default) =>
        TrustedHostConfigurationClient.GetStatusAsync(http, cancellationToken);

    public Task<TrustedGitHubAppConfigurationStatus> ValidateConfigurationAsync(
        TrustedGitHubAppConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        TrustedHostConfigurationClient.SendAsync(
            http, authentication.Value, "source-access", "validate", configuration, cancellationToken);

    public Task<TrustedGitHubAppConfigurationStatus> ActivateConfigurationAsync(
        TrustedGitHubAppConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        TrustedHostConfigurationClient.SendAsync(
            http, authentication.Value, "source-access", "activate", configuration, cancellationToken);

    public async Task<IReadOnlyList<TrustedRepositoryDescriptor>> ListRepositoriesAsync(
        long installationId,
        CancellationToken cancellationToken = default) =>
        await TrustedHostRepositoryReader.ListAsync(http, installationId, cancellationToken);

    public async Task<TrustedInstallationDescriptor> DescribeInstallationAsync(
        long installationId,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(
            "internal/v2/installations/describe",
            new GitHubInstallationRequest(installationId),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<GitHubInstallationDescriptor>(cancellationToken)
            ?? throw new InvalidOperationException("GitHost returned an empty installation response.");
        return new TrustedInstallationDescriptor(
            result.InstallationId, result.AccountId, result.AccountLogin,
            result.AccountType, result.Suspended, result.SuspendedReason);
    }

    public async Task<TrustedMergeResult> MergeAsync(
        TrustedMergeRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(
            "internal/v2/pull-requests/merge-exact",
            new GitHubMergeRequest(
                request.InstallationId,
                request.Owner,
                request.Repository,
                request.PullRequestNumber,
                request.ExpectedHeadSha,
                request.IdempotencyKey),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<GitHubMergeResult>(cancellationToken)
            ?? throw new InvalidOperationException("GitHost returned an empty merge response.");
        return new TrustedMergeResult(
            result.Merged, result.HeadMatched, result.MergeCommitSha,
            result.FailureCode, result.FailureMessage);
    }

    public async Task<TrustedWorkspaceSnapshot> PrepareWorkspaceAsync(
        TrustedWorkspaceSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(
            "internal/v2/workspaces/prepare",
            new GitHubWorkspacePrepareRequest(
                request.InstallationId,
                request.Owner,
                request.Repository,
                request.DefaultBranch,
                request.WorkspaceId,
                request.DeterministicBranch,
                request.ExpectedCommitSha,
                request.IdempotencyKey),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(MaximumWorkspaceArchiveBytes, cancellationToken);
        var archive = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return new TrustedWorkspaceSnapshot(
            RequiredHeader(response, WorkspaceSnapshotHeaders.WorkspaceKey),
            RequiredHeader(response, WorkspaceSnapshotHeaders.BaseCommitSha),
            bool.Parse(RequiredHeader(response, WorkspaceSnapshotHeaders.Resumed)),
            archive,
            RequiredHeader(response, WorkspaceSnapshotHeaders.ArtifactSha256),
            int.Parse(RequiredHeader(response, WorkspaceSnapshotHeaders.ArtifactFileCount)),
            long.Parse(RequiredHeader(response, WorkspaceSnapshotHeaders.ArtifactTotalBytes)));
    }

    private static string RequiredHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? values.Single()
            : throw new InvalidOperationException($"GitHost omitted required workspace metadata: {name}.");
}

public sealed class TrustedProvisioningHostClient(
    HttpClient http,
    IOptions<TrustedServiceAuthenticationOptions> authentication) : ITrustedProvisioningHostClient
{
    public Task<TrustedGitHubAppConfigurationStatus> GetConfigurationStatusAsync(
        CancellationToken cancellationToken = default) =>
        TrustedHostConfigurationClient.GetStatusAsync(http, cancellationToken);

    public Task<TrustedGitHubAppConfigurationStatus> ValidateConfigurationAsync(
        TrustedGitHubAppConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        TrustedHostConfigurationClient.SendAsync(
            http, authentication.Value, "provisioner", "validate", configuration, cancellationToken);

    public Task<TrustedGitHubAppConfigurationStatus> ActivateConfigurationAsync(
        TrustedGitHubAppConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        TrustedHostConfigurationClient.SendAsync(
            http, authentication.Value, "provisioner", "activate", configuration, cancellationToken);

    public async Task<IReadOnlyList<TrustedRepositoryDescriptor>> ListRepositoriesAsync(
        long installationId,
        CancellationToken cancellationToken = default) =>
        await TrustedHostRepositoryReader.ListAsync(http, installationId, cancellationToken);

    public async Task<TrustedInstallationDescriptor> DescribeInstallationAsync(
        long installationId,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(
            "internal/v2/installations/describe",
            new GitHubInstallationRequest(installationId),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<GitHubInstallationDescriptor>(cancellationToken)
            ?? throw new InvalidOperationException("ProvisionerHost returned an empty installation response.");
        return new TrustedInstallationDescriptor(
            result.InstallationId, result.AccountId, result.AccountLogin,
            result.AccountType, result.Suspended, result.SuspendedReason);
    }

    public async Task<TrustedRepositoryProvisioningResult> ProvisionAsync(
        TrustedRepositoryProvisioningRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(
            "internal/v2/repositories/provision-private",
            new GitHubProvisionRepositoryRequest(
                request.InstallationId,
                request.OrganizationLogin,
                request.RepositoryName,
                request.Description,
                request.TemplateOwner,
                request.TemplateRepository,
                request.RequiredDefaultBranch,
                request.IdempotencyKey),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<GitHubProvisionRepositoryResult>(cancellationToken)
            ?? throw new InvalidOperationException("ProvisionerHost returned an empty provisioning response.");
        return new TrustedRepositoryProvisioningResult(
            result.Created, result.Quarantined, result.RepositoryId,
            result.Owner, result.Repository, result.DefaultBranch,
            result.FailureCode, result.FailureMessage);
    }
}

internal static class TrustedHostConfigurationClient
{
    public static async Task<TrustedGitHubAppConfigurationStatus> GetStatusAsync(
        HttpClient http,
        CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync("internal/v2/configuration/status", cancellationToken);
        response.EnsureSuccessStatusCode();
        return Map(await response.Content.ReadFromJsonAsync<GitHubAppConfigurationStatus>(cancellationToken)
            ?? throw new InvalidOperationException("The trusted host returned no configuration status."));
    }

    public static async Task<TrustedGitHubAppConfigurationStatus> SendAsync(
        HttpClient http,
        TrustedServiceAuthenticationOptions authentication,
        string hostKind,
        string operation,
        TrustedGitHubAppConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var envelope = GitHubAppConfigurationEnvelope.Seal(
            new GitHubAppConfigurationPayload(
                configuration.AppId, configuration.PrivateKeyBase64, configuration.Revision),
            authentication,
            hostKind);
        using var response = await http.PostAsJsonAsync(
            $"internal/v2/configuration/{operation}", envelope, cancellationToken);
        response.EnsureSuccessStatusCode();
        return Map(await response.Content.ReadFromJsonAsync<GitHubAppConfigurationStatus>(cancellationToken)
            ?? throw new InvalidOperationException("The trusted host returned no configuration result."));
    }

    private static TrustedGitHubAppConfigurationStatus Map(GitHubAppConfigurationStatus value) => new(
        value.Configured, value.AppId, value.Revision, value.AppSlug,
        value.AppName, value.FailureMessage);
}

internal static class TrustedHostRepositoryReader
{
    public static async Task<IReadOnlyList<TrustedRepositoryDescriptor>> ListAsync(
        HttpClient http,
        long installationId,
        CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(
            "internal/v2/repositories/list",
            new GitHubInstallationRequest(installationId),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<
            IReadOnlyList<GitHubRepositoryDescriptor>>(cancellationToken)
            ?? throw new InvalidOperationException("Trusted host returned an empty repository-list response.");
        return result.Select(repository => new TrustedRepositoryDescriptor(
            repository.RepositoryId,
            repository.Owner,
            repository.Name,
            repository.FullName,
            repository.CloneUrl,
            repository.DefaultBranch,
            repository.IsPrivate,
            repository.IsArchived,
            repository.IsTemplate)).ToList();
    }
}
