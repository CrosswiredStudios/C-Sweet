namespace CSweet.Contracts.Setup;

public sealed record ExecutionFleetAdministrationResponse(
    IReadOnlyList<ExecutionPoolResponse> Pools,
    IReadOnlyList<ExecutionNodeSummaryResponse> Nodes,
    IReadOnlyList<ExecutionAssignmentSummaryResponse> RecentAssignments,
    IReadOnlyList<AgentExecutionPoolOverrideResponse> InstallationOverrides);

public sealed record CreateExecutionPoolRequest(
    string Name,
    int MaximumActiveWorkloads,
    IReadOnlyDictionary<string, string> RequiredLabels,
    IReadOnlyList<string> AllowedBusinessIds);

public sealed record UpdateExecutionPoolRequest(
    string Name,
    bool IsEnabled,
    int MaximumActiveWorkloads,
    IReadOnlyDictionary<string, string> RequiredLabels,
    IReadOnlyList<string> AllowedBusinessIds,
    bool SetAsDefaultBuildPool,
    bool SetAsDefaultRuntimePool);

public sealed record UpdateAgentExecutionPoolRequest(Guid? ExecutionPoolId);

public sealed record AgentExecutionPoolOverrideResponse(
    Guid InstallationId,
    string AgentName,
    string BusinessId,
    Guid? ConfiguredExecutionPoolId,
    Guid EffectiveExecutionPoolId,
    string EffectiveExecutionPoolName);

public sealed record ExecutionFleetMutationResponse(
    bool Succeeded,
    string? ErrorCode,
    string Message);

public sealed record ExecutionAssignmentSummaryResponse(
    Guid Id,
    Guid ExecutionPoolId,
    Guid? ExecutionNodeId,
    Guid? AgentBuildJobId,
    Guid? AgentRuntimeInstanceId,
    string WorkloadKind,
    string Status,
    string ProviderId,
    string GuestImageDigest,
    int Attempt,
    long FencingEpoch,
    int ReservedCpuCount,
    int ReservedMemoryMb,
    int ReservedDiskMb,
    DateTimeOffset QueuedAt,
    DateTimeOffset? AssignedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? FailureCode);

public sealed record UpdateExecutionNodeLabelsRequest(IReadOnlyDictionary<string, string> Labels);
