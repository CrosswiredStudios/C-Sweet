namespace CSweet.Contracts.WorkManagement;

public sealed record IntegratedCandidateResponse(Guid PublicationId, Guid IntegrationReceiptId,
    Guid RepositoryId, string PublishedCommitSha, string IntegratedCommitSha, long ItemRevision);

public sealed record ResubmitIntegratedCandidateRequest(long ExpectedRevision, string ExpectedCommitSha,
    string IdempotencyKey, string Reason);
