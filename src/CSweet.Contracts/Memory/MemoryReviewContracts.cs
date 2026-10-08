using System.Text.Json.Serialization;

namespace CSweet.Contracts.Memory;

public sealed record MemoryClaimReviewResponse(Guid ClaimId, long Revision, string EvidenceToken,
    string Content, string Confirmation, string Sensitivity, bool CanConfirm, bool CanCorrect, bool CanReject,
    IReadOnlyList<Guid> SourceEpisodeIds, bool IsEntityValued = false);

public sealed record ReviewMemoryClaimRequest(Guid OperationId, long ExpectedRevision, string EvidenceToken,
    string Action, string? ReplacementValue = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MemoryClaimEntityCorrection? ReplacementEntity = null);

public sealed record MemoryClaimEntityCorrection(Guid EntityId, string EvidenceToken);
public sealed record MemoryClaimCorrectionTarget(Guid EntityId, string Name, string Type, string Sensitivity,
    string EvidenceToken, IReadOnlyList<Guid> SourceEpisodeIds);

public sealed record ReviewMemoryClaimResponse(Guid ReceiptId, Guid ClaimId, Guid ResultClaimId,
    long ResultRevision, string Action, DateTimeOffset ReviewedAt, bool WasReplay);
