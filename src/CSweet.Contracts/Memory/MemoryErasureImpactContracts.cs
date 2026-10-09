namespace CSweet.Contracts.Memory;

/// <summary>A read-only review bound to the current complete inventory.</summary>
public sealed record MemoryErasureImpactResponse(Guid EpisodeId, int RecordCount,
    IReadOnlyList<MemoryErasureAudienceImpact> Audiences, string? BlockedReason)
{
    public MemoryErasureExecutionImpact? Execution { get; init; }
    public string? EvidenceToken { get; init; }
    public int DiagnosticTurns { get; init; }
    public string? ApplyBlockedReason { get; init; }
}

public sealed record MemoryErasureExecutionImpact(int CaptureSources, int ExtractionJobs, int WorkItems,
    int Runtimes, int ActiveRuntimes, string? BlockedReason)
{
    public int ModelRuns { get; init; }
}

public sealed record MemoryErasureAudienceImpact(string Scope, Guid? EmployeeId, string AudienceName,
    IReadOnlyList<MemoryErasureKindCount> Records);

public sealed record MemoryErasureKindCount(string Kind, int Count);

public sealed record EraseMemorySourceRequest(Guid OperationId, string EvidenceToken);
public sealed record MemoryErasureResponse(Guid ReceiptId, Guid OperationId, Guid EpisodeId, int ErasedRecords,
    int ErasedRevisions, int ClearedJobs, int ClearedWorks, int ClearedDiagnosticTurns, int PendingRuntimes,
    string Status, DateTimeOffset ErasedAt, bool WasReplay)
{
    public int ClearedModelRuns { get; init; }
}

/// <summary>Only the requesting human's operation identity; availability is not cleanup completion.</summary>
public sealed record MemoryErasureOperationSummary(Guid OperationId, DateTimeOffset ErasedAt, string Availability);
public sealed record MemoryErasureOperationPage(IReadOnlyList<MemoryErasureOperationSummary> Items, Guid? NextBeforeReceiptId);
