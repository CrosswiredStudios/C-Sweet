using CSweet.Contracts.Setup;

namespace CSweet.UI.Services;

public interface ISetupApiClient
{
    Task<LocalOfficeSetupActionResponse> CancelOfficeUpdateAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    Task<SetupStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<ExecutionCapacityOnboardingResponse> GetExecutionCapacityStatusAsync(CancellationToken cancellationToken = default);
    Task<ExecutionCapacityOnboardingResponse> RefreshOfficeStorageAsync(CancellationToken cancellationToken = default) => GetExecutionCapacityStatusAsync(cancellationToken);
    Task<ExecutionCapacityActionResponse> SelectExecutionModeAsync(string mode, CancellationToken cancellationToken = default);
    Task<ExecutionCapacityActionResponse> CreateExecutionEnrollmentAsync(CancellationToken cancellationToken = default);
    Task<LocalOfficeSetupActionResponse> CreateLocalOfficeSetupSessionAsync(
        CreateLocalOfficeSetupSessionRequest request,
        CancellationToken cancellationToken = default);
    Task<LocalOfficeSetupActionResponse> LaunchLocalOfficeSetupSessionAsync(
        Guid sessionId,
        LaunchLocalOfficeSetupRequest request,
        CancellationToken cancellationToken = default);
    Task<LocalOfficeSetupActionResponse> GetLocalOfficeSetupSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);
    Task<LocalOfficeSetupActionResponse> GetActiveLocalOfficeSetupSessionAsync(
        CancellationToken cancellationToken = default);
    Task<LocalOfficeSetupActionResponse> RefreshLocalOfficeSetupSessionHandoffAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);
    Task<LocalOfficeSetupActionResponse> SelectLocalOfficeRecoveryAsync(
        Guid sessionId,
        string action,
        CancellationToken cancellationToken = default);
    Task<ExecutionCapacityActionResponse> RevokeExecutionEnrollmentAsync(Guid enrollmentId, CancellationToken cancellationToken = default);
    Task<ExecutionCapacityActionResponse> ApproveExecutionNodeAsync(Guid nodeId, CancellationToken cancellationToken = default);
    Task<ExecutionCapacityActionResponse> RejectExecutionNodeAsync(Guid nodeId, CancellationToken cancellationToken = default);
    Task<SetupActionResponse> CompleteStepAsync(string key, CancellationToken cancellationToken = default);
    Task<SetupActionResponse> CompleteSetupAsync(CancellationToken cancellationToken = default);
}
