namespace CSweet.Domain.Core;

/// <summary>
/// Permanent, content-free evidence that a conversation message was edited, deleted,
/// or excluded by a reviewed memory erasure.
/// No foreign keys: source, conversation and organization deletion must not erase it.
/// A replacement message needs a new identity before it can be captured as memory.
/// </summary>
public sealed class MemorySourceInvalidation
{
    public Guid SourceMessageId { get; set; }
    public Guid PreviousConversationId { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public DateTimeOffset InvalidatedAt { get; set; }
}
