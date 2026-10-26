namespace CSweet.Contracts.Agents;

public sealed record AgentInstallationResponse(
    Guid Id,
    Guid PackageVersionId,
    string BusinessId,
    string AgentId,
    string AgentName,
    string AgentVersion,
    string PublisherName,
    string CommitSha,
    bool IsEnabled,
    IReadOnlyList<string> GrantedCapabilities,
    IReadOnlyList<string> GrantedSubscriptions,
    IReadOnlyList<string> GrantedPublications,
    IReadOnlyList<string> GrantedPermissions,
    IReadOnlyList<string> GrantedNetworkAccess,
    int MemoryMb,
    int CpuPercent,
    // Null for broker-only connectors; VM settings do not apply to their operations.
    AgentScheduleResponse? Schedule,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    AgentBuildSummaryResponse? Build = null,
    AgentRuntimeRunResponse? LatestRuntime = null)
{
    public bool PublicInternetGranted { get; init; }
    public bool PublicInternetRequested { get; init; }
    public long GrantRevision { get; init; }
    public bool CanReviewRuntimeInternet { get; init; }
    public string? ImageUrl { get; init; }
    public string? AccentColor { get; init; }
    public string? RoleName { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];
    public string PluginKind { get; init; } = "Agent";
    public string InstallationScope { get; init; } = "Organization";
    public Guid InstallationKey { get; init; }
    public int RevisionNumber { get; init; } = 1;
    public string RevisionStatus { get; init; } = "Active";
    // Removal has fenced access but still awaits account revocation/cleanup confirmation.
    public bool RemovalPending { get; init; }
    public string SetupState { get; init; } = "Ready";
    public string? SetupFlowId { get; init; }
    public string? SetupStepId { get; init; }
    public IReadOnlyList<CSweet.Contracts.Plugins.PluginConnectionDeclaration> ConnectionDeclarations { get; init; } = [];
    public CSweet.Contracts.Plugins.PluginAccountManagement? Accounts { get; init; }
}

public sealed record AgentBuildSummaryResponse(
    Guid BuildJobId,
    string Status,
    int Attempt,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    bool HasLog,
    string? FailureMessage,
    IReadOnlyList<AgentBuildStepResponse>? Steps = null)
{
    public string SourceMode { get; init; } = "SourceBuild";
    public string? ReleaseTag { get; init; }
    public string? ReleaseAssetName { get; init; }
}

public sealed record AgentBuildStepResponse(
    string Key,
    string Label,
    string Status,
    string? Detail,
    string? Error,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record AgentRuntimeRunResponse(
    Guid Id,
    Guid TickId,
    string Status,
    string? Reason,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? McpSessionEstablishedAt,
    DateTimeOffset? CompletionReportedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<AgentRuntimeEventResponse> Events,
    string? LogExcerpt = null)
{
    public string NetworkMode { get; init; } = "not-running";
    public IReadOnlyList<string> ImageTools { get; init; } = [];
    public int AllocatedMemoryMb { get; init; }
    public long PeakWorkloadMemoryBytes { get; init; }
    public string? ResourceFailureCode { get; init; }
}

public sealed record AgentRuntimeEventResponse(
    string Status,
    string? Reason,
    DateTimeOffset OccurredAt);

public sealed record AgentBuildLogResponse(
    Guid BuildJobId,
    string Status,
    string Content,
    bool IsTruncated);
