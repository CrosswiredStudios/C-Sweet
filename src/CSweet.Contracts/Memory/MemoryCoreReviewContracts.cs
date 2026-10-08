namespace CSweet.Contracts.Memory;

public sealed record MemoryCoreReviewResponse(Guid BlockId, long Revision, string EvidenceToken, string Name,
    string Content, int BlockRevision, bool IsPinned, string Confirmation, string Sensitivity,
    bool CanConfirm, bool CanCorrect, bool CanReject, IReadOnlyList<Guid> SourceEpisodeIds);
public sealed record MemoryCoreCorrection(string Content, bool IsPinned);
public sealed record ReviewMemoryCoreRequest(Guid OperationId, long ExpectedRevision, string EvidenceToken,
    string Action, MemoryCoreCorrection? Correction = null);
public sealed record ReviewMemoryCoreResponse(Guid ReceiptId, Guid BlockId, long ResultRevision, string Action,
    DateTimeOffset ReviewedAt, bool WasReplay);
