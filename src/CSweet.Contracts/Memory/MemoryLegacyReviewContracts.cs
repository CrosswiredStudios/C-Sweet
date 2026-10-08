namespace CSweet.Contracts.Memory;

public sealed record MemoryLegacyReviewResponse(Guid EpisodeId, long Revision, string EvidenceToken,
    string Content, string Source, string Sensitivity, string MinimumSensitivity, bool CanEstablishEvidence);
public sealed record ReviewMemoryLegacyRequest(Guid OperationId, long ExpectedRevision, string EvidenceToken, string Sensitivity);
public sealed record ReviewMemoryLegacyResponse(Guid ReceiptId, Guid EpisodeId, long Revision, DateTimeOffset ReviewedAt, bool WasReplay);
