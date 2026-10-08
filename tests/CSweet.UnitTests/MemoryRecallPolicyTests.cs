using CSweet.Infrastructure.Core;
using CSweet.Memory;

namespace CSweet.UnitTests;

public sealed class MemoryRecallPolicyTests
{
    [Theory]
    [InlineData(MemoryConfirmationState.NotRequired, MemorySensitivity.Personal, true)]
    [InlineData(MemoryConfirmationState.Confirmed, MemorySensitivity.Internal, true)]
    [InlineData(MemoryConfirmationState.Pending, MemorySensitivity.Internal, false)]
    [InlineData(MemoryConfirmationState.Rejected, MemorySensitivity.Public, false)]
    [InlineData(MemoryConfirmationState.Confirmed, MemorySensitivity.Confidential, false)]
    [InlineData((MemoryConfirmationState)99, MemorySensitivity.Internal, false)]
    [InlineData(MemoryConfirmationState.Confirmed, (MemorySensitivity)(-1), false)]
    public void EligibilityRequiresKnownStateAndSensitivity(MemoryConfirmationState state,
        MemorySensitivity sensitivity, bool expected)
    {
        var candidate = Candidate() with { Confirmation = state, Sensitivity = sensitivity };
        Assert.Equal(expected, MemoryRecallPolicy.IsEligible(candidate, MemorySensitivity.Personal, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ValidityIsInclusiveAtStartAndExclusiveAtEnd()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(MemoryRecallPolicy.IsEligible(Candidate() with { ValidFrom = now }, MemorySensitivity.Internal, now));
        Assert.False(MemoryRecallPolicy.IsEligible(Candidate() with { ValidFrom = now.AddTicks(1) }, MemorySensitivity.Internal, now));
        Assert.False(MemoryRecallPolicy.IsEligible(Candidate() with { ValidTo = now }, MemorySensitivity.Internal, now));
        Assert.True(MemoryRecallPolicy.IsEligible(Candidate() with { ValidTo = now.AddTicks(1) }, MemorySensitivity.Internal, now));
    }

    private static MemoryCandidate Candidate() => new(Guid.NewGuid(), MemoryLayer.Semantic, "content", 1,
        MemoryTrustTier.AgentInference, MemoryConfirmationState.NotRequired, MemorySensitivity.Internal,
        null, null, [], "semantic");
}
