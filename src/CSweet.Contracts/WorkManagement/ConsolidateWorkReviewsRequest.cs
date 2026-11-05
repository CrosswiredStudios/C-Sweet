namespace CSweet.Contracts.WorkManagement;

public sealed record ConsolidateWorkReview(Guid WorkItemId, long ExpectedRevision, string CanonicalStageKey,
    IReadOnlyList<string> AcceptanceCriteria);
public sealed record ConsolidateWorkReviewsRequest(Guid CanonicalWorkItemId, long ExpectedCanonicalRevision,
    IReadOnlyList<ConsolidateWorkReview> Reviews, string Reason, string IdempotencyKey);
public sealed record ConsolidateWorkReviewsResult(Guid CanonicalWorkItemId, IReadOnlyList<Guid> CancelledReviewIds,
    IReadOnlyList<Guid> RedirectedDependentIds);
