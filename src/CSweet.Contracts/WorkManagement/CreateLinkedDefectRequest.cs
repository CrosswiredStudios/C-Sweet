namespace CSweet.Contracts.WorkManagement;

public sealed record CreateLinkedDefectRequest(long ExpectedRevision, Guid QaItemId, long ExpectedQaRevision,
    string IdempotencyKey, string Title, string Description);
