namespace CSweet.Infrastructure.Core;

/// <summary>A queued payload's immutable recall evidence no longer authorizes delivery.</summary>
public sealed class MemoryRecallDeliveryRejectedException() : InvalidOperationException(SafeMessage)
{
    public const string Code = "memory.recall_stale";
    public const string SafeMessage = "The recalled context for this queued request is no longer valid. Please retry as a new chat turn.";
}
