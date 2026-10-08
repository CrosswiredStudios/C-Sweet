namespace CSweet.Contracts.Memory;

public sealed record MemoryReextractionCandidate(Guid JobId, Guid EpisodeId, int InputGeneration, string Status,
    DateTimeOffset CreatedAt, bool HasPreviousAcceptedExtraction, string Audience);
public sealed record MemoryReextractionCandidatePage(IReadOnlyList<MemoryReextractionCandidate> Items, string? NextCursor);
public sealed record MemoryReextractionPreview(Guid JobId, Guid EpisodeId, int InputGeneration, string EvidenceToken,
    string Content, string Source, string Audience, string Sensitivity, bool CanQueue, string? BlockedReason,
    int ExistingRecords, bool HasPreviousAcceptedExtraction);
public sealed record ReviewMemoryReextractionRequest(Guid OperationId, int ExpectedInputGeneration, string EvidenceToken,
    string PreservationPolicy);
public sealed record ReviewMemoryReextractionResponse(Guid ReceiptId, Guid PreviousJobId, Guid JobId, Guid EpisodeId,
    int InputGeneration, string Status, bool Replayed);
