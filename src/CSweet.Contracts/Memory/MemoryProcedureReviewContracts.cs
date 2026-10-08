namespace CSweet.Contracts.Memory;

public sealed record MemoryProcedureReviewResponse(Guid ProcedureId, long Revision, string EvidenceToken,
    string Name, string Procedure, string? Applicability, int Version, string Confirmation, string Sensitivity,
    bool CanConfirm, bool CanCorrect, bool CanReject, IReadOnlyList<Guid> SourceEpisodeIds);
public sealed record MemoryProcedureCorrection(string Name, string Procedure, string? Applicability);
public sealed record ReviewMemoryProcedureRequest(Guid OperationId, long ExpectedRevision, string EvidenceToken,
    string Action, MemoryProcedureCorrection? Correction = null);
public sealed record ReviewMemoryProcedureResponse(Guid ReceiptId, Guid ProcedureId, Guid ResultProcedureId,
    long ResultRevision, string Action, DateTimeOffset ReviewedAt, bool WasReplay);
