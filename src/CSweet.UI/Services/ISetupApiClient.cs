using CSweet.Contracts.Setup;

namespace CSweet.UI.Services;

public interface ISetupApiClient
{
    Task<SetupStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<ExecutionCapacityOnboardingResponse> GetExecutionCapacityStatusAsync(CancellationToken cancellationToken = default);
    Task<ExecutionCapacityActionResponse> SelectExecutionModeAsync(string mode, CancellationToken cancellationToken = default);
    Task<ExecutionCapacityActionResponse> CreateExecutionEnrollmentAsync(CancellationToken cancellationToken = default);
    Task<ExecutionCapacityActionResponse> InstallLocalExecutionNodeAsync(string enrollmentToken, CancellationToken cancellationToken = default);
    Task<ExecutionCapacityActionResponse> RevokeExecutionEnrollmentAsync(Guid enrollmentId, CancellationToken cancellationToken = default);
    Task<ExecutionCapacityActionResponse> ApproveExecutionNodeAsync(Guid nodeId, CancellationToken cancellationToken = default);
    Task<ExecutionCapacityActionResponse> RejectExecutionNodeAsync(Guid nodeId, CancellationToken cancellationToken = default);
    Task<SetupActionResponse> CompleteStepAsync(string key, CancellationToken cancellationToken = default);
    Task<SetupActionResponse> CompleteSetupAsync(CancellationToken cancellationToken = default);
}
