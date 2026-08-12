namespace CSweet.Application.Setup;

public interface ILocalExecutionNodeProvisioner
{
    LocalExecutionNodeProvisioningProgress? GetProgress();

    Task<LocalExecutionNodeProvisioningResult> PrepareAsync(
        string controlPlaneUrl,
        string enrollmentToken,
        CancellationToken cancellationToken = default);
}

public sealed record LocalExecutionNodeProvisioningResult(
    bool Succeeded,
    string? ErrorCode,
    string Message,
    bool RequiresElevation);

public sealed record LocalExecutionNodeProvisioningProgress(
    Guid JobId,
    string Platform,
    string State,
    string PhaseKey,
    string PhaseDisplayName,
    string Message,
    int PercentComplete,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    bool RequiresRestart,
    string? ErrorCode,
    string? ErrorMessage,
    int? OwnerProcessId,
    int? EstimatedRemainingMinimumSeconds = null,
    int? EstimatedRemainingMaximumSeconds = null);
