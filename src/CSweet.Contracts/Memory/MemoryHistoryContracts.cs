namespace CSweet.Contracts.Memory;

public sealed record MemoryHistoryLink(string Kind, Guid Id, string Label);
public sealed record MemoryHistoryReview(Guid ReceiptId, string Action, Guid ReviewerId, string ReviewerName,
    DateTimeOffset ReviewedAt, MemoryHistoryLink? RelatedRecord);
public sealed record MemoryHistorySnapshot(long Revision, string Operation, DateTimeOffset RecordedAt, string Title,
    string Content, string? Applicability, string? State, string? Trust, string? RecordedSensitivity,
    DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo, IReadOnlyList<MemoryHistoryLink> Sources,
    IReadOnlyList<MemoryHistoryLink> RelatedRecords, IReadOnlyList<MemoryHistoryReview> Reviews);
public sealed record MemoryHistoryPage(string Kind, Guid RecordId, string Scope, IReadOnlyList<MemoryHistorySnapshot> Items,
    long? NextAfterRevision);
