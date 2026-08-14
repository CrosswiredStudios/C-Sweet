using CSweet.Domain.Setup;

namespace CSweet.Application.Setup;

public sealed record ExecutionWorkloadRequest(
    ExecutionWorkloadKind WorkloadKind,
    Guid? AgentBuildJobId,
    Guid? AgentRuntimeInstanceId,
    Guid? ExecutionPoolId,
    string? BusinessId,
    string? PreferredProviderId,
    string GuestImageDigest,
    string? ArtifactDigest,
    int CpuCount,
    int MemoryMb,
    int DiskMb,
    string SpecificationJson,
    bool AllowDevelopmentSecurityPosture = false);

public sealed record ExecutionWorkloadReference(Guid AssignmentId, long FencingEpoch);

public sealed record ExecutionAssignmentLease(
    Guid AssignmentId,
    Guid NodeId,
    long FencingEpoch,
    ExecutionWorkloadKind WorkloadKind,
    string ProviderId,
    string GuestImageDigest,
    string? ArtifactDigest,
    string SpecificationJson,
    string SpecificationDigest,
    DateTimeOffset LeaseExpiresAt);

public sealed record ExecutionWorkloadResult(
    string? ProviderInstanceId,
    string? LogExcerpt);

public interface IExecutionWorkloadOrchestrator
{
    Task<ExecutionWorkloadReference> SubmitAsync(
        ExecutionWorkloadRequest request,
        CancellationToken cancellationToken = default);

    Task<int> AssignPendingAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExecutionAssignmentLease>> GetNodeAssignmentsAsync(
        Guid nodeId,
        long sessionEpoch,
        CancellationToken cancellationToken = default);

    Task<string?> IssueArtifactReadGrantAsync(
        Guid nodeId,
        Guid assignmentId,
        long fencingEpoch,
        CancellationToken cancellationToken = default);

    Task<bool> RenewLeaseAsync(
        Guid nodeId,
        Guid assignmentId,
        long fencingEpoch,
        CancellationToken cancellationToken = default);

    Task<bool> ReportStatusAsync(
        Guid nodeId,
        Guid assignmentId,
        long fencingEpoch,
        ExecutionAssignmentStatus status,
        string? failureCode,
        string? sanitizedFailure,
        ExecutionWorkloadResult? result,
        CancellationToken cancellationToken = default);

    Task<bool> CancelAsync(
        Guid assignmentId,
        string reason,
        CancellationToken cancellationToken = default);

    Task<int> FenceExpiredAsync(CancellationToken cancellationToken = default);
}
