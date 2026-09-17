namespace CSweet.Contracts.Analytics;

public sealed record EfficiencyUsage(
    long ModelCalls, long InputTokens, long OutputTokens, long CachedInputTokens, long ReasoningTokens,
    long InputReportedCalls, long OutputReportedCalls, long FailedCalls, long CancelledCalls,
    long LegacyCalls, long ProviderDurationMs)
{
    public long TotalTokens => InputTokens + OutputTokens;
    public long FullyReportedCalls { get; init; }
}

public sealed record EfficiencyLifecycle(DateTimeOffset? CreatedAt, DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt, long? LeadTimeMs, long? CycleTimeMs, long WaitingTimeMs,
    int ReopenCount, bool CompleteHistory);

public sealed record WorkEfficiencyRow(Guid Id, Guid? ParentId, Guid? WorkstreamId, string Kind,
    string Title, string Status, EfficiencyUsage Direct, EfficiencyUsage Total, EfficiencyLifecycle Lifecycle);

public sealed record WorkEfficiencyResponse(Guid OrganizationId, DateTimeOffset GeneratedAt,
    DateTimeOffset? From, DateTimeOffset? To, EfficiencyUsage BusinessTotal,
    EfficiencyUsage BusinessOverhead, EfficiencyUsage Unattributed,
    IReadOnlyList<WorkEfficiencyRow> Projects, IReadOnlyList<WorkEfficiencyRow> WorkItems,
    bool Truncated, string CoverageNote);

public sealed record EfficiencyCallResponse(Guid Id, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
    string Status, string? Model, string? AgentVersion, string Purpose, Guid? WorkItemId,
    long? InputTokens, long? OutputTokens, string AttributionKind, string MeasurementKind);

public sealed record EfficiencyActivityResponse(IReadOnlyList<EfficiencyCallResponse> Calls,
    int Offset, bool HasMore);
