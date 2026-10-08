namespace CSweet.Contracts.Memory;

public sealed record MemorySuppressionPreview(Guid EpisodeId, long Revision, string EvidenceToken,
    string Content, bool IsSuppressed, bool LegalHold);
public sealed record SuppressMemorySourceRequest(Guid OperationId, long ExpectedRevision, string EvidenceToken);
public sealed record SuppressMemorySourceResponse(Guid ReceiptId, Guid EpisodeId, long Revision, DateTimeOffset SuppressedAt, bool WasReplay);
