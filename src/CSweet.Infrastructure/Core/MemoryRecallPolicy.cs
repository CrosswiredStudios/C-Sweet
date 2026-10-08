using CSweet.Memory;

namespace CSweet.Infrastructure.Core;

/// <summary>Conservative eligibility rules shared by platform chat and broker recall.</summary>
public static class MemoryRecallPolicy
{
    // These are platform ceilings, never values taken from an agent's principal attributes.
    // Finer sensitivity grants and source-derived restrictions are separate policy work.
    public static MemorySensitivity MaximumSensitivity(MemoryPartition authorizedPartition) =>
        authorizedPartition.AgentId is null ? MemorySensitivity.Internal : MemorySensitivity.Personal;

    public static bool IsEligible(MemoryCandidate candidate, MemorySensitivity maximum, DateTimeOffset asOf) =>
        Enum.IsDefined(candidate.Layer) && double.IsFinite(candidate.Score) &&
        IsEligible(candidate.Confirmation, candidate.Sensitivity, candidate.ValidFrom, candidate.ValidTo, maximum, asOf);

    public static bool IsEligible(MemoryClaim claim, MemorySensitivity maximum, DateTimeOffset asOf) =>
        IsEligible(claim.Confirmation, claim.Sensitivity, claim.ValidFrom, claim.ValidTo, maximum, asOf);

    private static bool IsEligible(MemoryConfirmationState confirmation, MemorySensitivity sensitivity,
        DateTimeOffset? validFrom, DateTimeOffset? validTo, MemorySensitivity maximum, DateTimeOffset asOf) =>
        (confirmation is MemoryConfirmationState.NotRequired or MemoryConfirmationState.Confirmed) &&
        Enum.IsDefined(sensitivity) && sensitivity <= maximum &&
        (validFrom is null || validFrom <= asOf) && (validTo is null || validTo > asOf);

    public static string EscapeContent(string content) => content
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
