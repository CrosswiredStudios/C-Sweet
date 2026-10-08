namespace CSweet.Infrastructure.Core;

/// <summary>Server-detected context which must not survive into another model invocation.</summary>
public sealed class MemoryRuntimeResetRequiredException(string reasonCode) : Exception(SafeMessage)
{
    public const string FailureCode = "memory.runtime_reset";
    public const string SafeMessage = "The agent's memory context changed and its runtime must be replaced. Work already delivered was not replayed; review its outcome before trying again.";
    public const string LegacyEvidence = "memory.evidence_legacy";
    public const string RetainedEvidence = "memory.retained_evidence_invalid";
    public const string ReceiptCapacity = "memory.receipt_capacity";
    public const string ErasedEvidence = "memory.source_erased";
    public string ReasonCode { get; } = reasonCode;
}
