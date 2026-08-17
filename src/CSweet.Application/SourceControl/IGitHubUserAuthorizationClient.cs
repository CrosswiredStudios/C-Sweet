namespace CSweet.Application.SourceControl;

/// <summary>
/// Exchanges a one-time GitHub App OAuth code and proves that the authenticated GitHub user can
/// access the installation returned by GitHub. Tokens never cross this boundary in a response.
/// </summary>
public interface IGitHubUserAuthorizationClient
{
    Task<GitHubAuthorizedInstallation> VerifyInstallationAsync(
        PlatformGitHubUserAuthorizationConfiguration configuration,
        string code,
        long installationId,
        CancellationToken cancellationToken = default);
}

public sealed record GitHubAuthorizedInstallation(
    long InstallationId,
    long UserId,
    string UserLogin);
