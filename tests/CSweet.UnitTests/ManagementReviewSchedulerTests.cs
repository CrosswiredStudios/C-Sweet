using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;

namespace CSweet.UnitTests;

public sealed class ManagementReviewSchedulerTests
{
    [Fact]
    public void ExecutiveBriefingDispatchPayload_IsStableAcrossSchedulerRetries()
    {
        var requestId = Guid.NewGuid();
        var cycleId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 7, 29, 14, 0, 0, TimeSpan.Zero);
        var request = new ManagementCheckInRequestRecord
        {
            Id = requestId,
            ManagementCycleId = cycleId,
            CheckInType = "ExecutiveBriefing",
            CreatedAt = createdAt,
            DueAt = createdAt.AddHours(2)
        };
        var cycle = new ManagementCycle
        {
            Id = cycleId,
            TimeZone = "UTC"
        };

        var due = ManagementReviewScheduler.CreateExecutiveBriefingDueEvent(request, cycle);

        Assert.Equal(requestId, due.RequestId);
        Assert.Equal(createdAt.AddDays(-1), due.PeriodStart);
        Assert.Equal(createdAt, due.PeriodEnd);
        Assert.Equal(request.DueAt, due.DueAt);
    }

    [Fact]
    public void ExecutiveBriefingDispatchKey_IsStablePerAttemptAndUniqueAcrossAttempts()
    {
        var requestId = Guid.NewGuid();

        var first = ManagementReviewScheduler.ExecutiveBriefingDispatchKey(requestId, 1);
        var replay = ManagementReviewScheduler.ExecutiveBriefingDispatchKey(requestId, 1);
        var retry = ManagementReviewScheduler.ExecutiveBriefingDispatchKey(requestId, 2);

        Assert.Equal(first, replay);
        Assert.NotEqual(first, retry);
        Assert.Equal($"executive-briefing:{requestId:N}:dispatch:1", first);
    }
}
