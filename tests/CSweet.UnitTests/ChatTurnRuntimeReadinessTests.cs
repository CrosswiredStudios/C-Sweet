using CSweet.Api.Chat;

namespace CSweet.UnitTests;

public sealed class ChatTurnRuntimeReadinessTests
{
    [Fact]
    public void RemainingTurnTime_IsAnchoredToOriginalCreationTime()
    {
        var createdAt = new DateTimeOffset(2026, 8, 7, 21, 30, 0, TimeSpan.Zero);

        var remaining = ChatTurnWorker.RemainingTurnTime(
            createdAt,
            TimeSpan.FromMinutes(30),
            createdAt.AddMinutes(28));
        var expired = ChatTurnWorker.RemainingTurnTime(
            createdAt,
            TimeSpan.FromMinutes(30),
            createdAt.AddMinutes(31));

        Assert.Equal(TimeSpan.FromMinutes(2), remaining);
        Assert.True(expired < TimeSpan.Zero);
    }
}
