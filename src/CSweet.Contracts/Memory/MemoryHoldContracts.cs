namespace CSweet.Contracts.Memory;

public sealed record MemoryHoldPreview(Guid EpisodeId, long Revision, string EvidenceToken,
    string Content, bool LegalHold, bool CanRelease)
{
    public bool IsTransferred { get; init; }
    public int UpstreamSources { get; init; }
    public int HeldUpstreamSources { get; init; }
    public string? ReleaseBlocker { get; init; }
}
public sealed record ReviewMemoryHoldRequest(Guid OperationId, long ExpectedRevision, string EvidenceToken, bool LegalHold);
public sealed record ReviewMemoryHoldResponse(Guid ReceiptId, Guid EpisodeId, long Revision,
    bool LegalHold, DateTimeOffset ReviewedAt, bool WasReplay);
