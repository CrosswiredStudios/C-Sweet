namespace CSweet.Contracts.Memory;

public sealed record MemoryIngestionCandidate(Guid EpisodeId, string Source, DateTimeOffset OccurredAt, string? Audience = null);
public sealed record MemoryIngestionCandidatePage(IReadOnlyList<MemoryIngestionCandidate> Items, string? NextCursor);
public sealed record MemoryIngestionRecoveryPreview(Guid EpisodeId, long Revision, string EvidenceToken,
    string Content, string Source, string Sensitivity, bool CanQueue, string? BlockedReason, string? Audience = null,
    int ExistingRecords = 0, string? RequiredReconciliationPolicy = null);
public sealed record RecoverMemoryIngestionRequest(Guid OperationId, long ExpectedRevision, string EvidenceToken,
    string? ReconciliationPolicy = null);
public sealed record RecoverMemoryIngestionResponse(Guid ReceiptId, Guid EpisodeId, Guid JobId, string Status, bool Replayed);
