using CSweet.Contracts.SourceControl;
using CSweet.Domain.Setup;

namespace CSweet.Application.SourceControl;

public interface ISourceControlPlatformConfigurationProvider
{
    Task<SourceControlPlatformReadiness> GetReadinessAsync(CancellationToken cancellationToken = default);
    Task<string> GetInstallUrlAsync(PlatformGitHubAppKind kind, CancellationToken cancellationToken = default);
    Task<PlatformGitHubUserAuthorizationConfiguration> GetUserAuthorizationAsync(
        PlatformGitHubAppKind kind,
        CancellationToken cancellationToken = default);
}

public sealed record PlatformGitHubUserAuthorizationConfiguration(
    string ClientId,
    string ClientSecret);

public interface ISourceControlPlatformSetupService : ISourceControlPlatformConfigurationProvider
{
    Task<PlatformSourceControlSetupResponse> GetAsync(
        Guid applicationUserId,
        CancellationToken cancellationToken = default);
    Task<PlatformSourceControlSetupResponse> StartAsync(
        Guid applicationUserId,
        StartPlatformSourceControlSetupRequest request,
        CancellationToken cancellationToken = default);
    Task<PlatformSourceControlSetupResponse> ConfirmOrganizationAsync(
        Guid applicationUserId,
        Guid sessionId,
        ConfirmPlatformOrganizationRequest request,
        CancellationToken cancellationToken = default);
    Task<PlatformSourceControlSetupResponse> ConfirmReviewAsync(
        Guid applicationUserId,
        Guid sessionId,
        PlatformGitHubAppKind kind,
        ConfirmPlatformAppReviewRequest request,
        CancellationToken cancellationToken = default);
    Task<PlatformGitHubManifestLaunchResponse> CreateManifestAsync(
        Guid applicationUserId,
        Guid sessionId,
        PlatformGitHubAppKind kind,
        CancellationToken cancellationToken = default);
    Task<PlatformGitHubManifestCompletion> CompleteManifestAsync(
        Guid applicationUserId,
        string code,
        string state,
        CancellationToken cancellationToken = default);
    Task<PlatformSourceControlSetupResponse> ConfirmAppAsync(
        Guid applicationUserId,
        Guid sessionId,
        PlatformGitHubAppKind kind,
        ConfirmPlatformAppRequest request,
        CancellationToken cancellationToken = default);
    Task<PlatformSourceControlSetupResponse> ChooseProvisionerAsync(
        Guid applicationUserId,
        Guid sessionId,
        ChoosePlatformProvisionerRequest request,
        CancellationToken cancellationToken = default);
    Task<PlatformSourceControlSetupResponse> ActivateAsync(
        Guid applicationUserId,
        Guid sessionId,
        ActivatePlatformSourceControlRequest request,
        CancellationToken cancellationToken = default);
    Task<PlatformSourceControlSetupResponse> CancelAsync(
        Guid applicationUserId,
        Guid sessionId,
        CancelPlatformSourceControlSetupRequest request,
        CancellationToken cancellationToken = default);
    Task ReconcileAsync(CancellationToken cancellationToken = default);
}

public sealed record PlatformGitHubManifestCompletion(Guid SessionId, string PublicBaseUrl);

public interface IPlatformGitHubManifestClient
{
    Task<PlatformGitHubManifestConversion> ConvertAsync(
        string code,
        CancellationToken cancellationToken = default);
}

public sealed record PlatformGitHubManifestConversion(
    long AppId,
    string AppName,
    string AppSlug,
    string PrivateKeyPem,
    string ClientId,
    string ClientSecret);
