namespace CSweet.Contracts.Memory;

public sealed record MemoryEnrichmentJobResponse(Guid Id, Guid ConversationId, string Status, int Attempts,
    int RetryGeneration, DateTimeOffset CreatedAt, DateTimeOffset NextAttemptAt, bool HasAcceptedExtraction,
    string? FailureCode, string JobKind = "conversation", Guid? EpisodeId = null);

public sealed record MemoryEnrichmentJobPageResponse(IReadOnlyList<MemoryEnrichmentJobResponse> Items, string? NextCursor);

public sealed record RetryMemoryEnrichmentRequest(Guid OperationId, int ExpectedRetryGeneration);

public sealed record RetryMemoryEnrichmentResponse(MemoryEnrichmentJobResponse Job, bool Replayed);
