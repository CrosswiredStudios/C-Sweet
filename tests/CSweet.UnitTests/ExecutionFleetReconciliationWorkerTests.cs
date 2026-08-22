using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;

namespace CSweet.UnitTests;

public sealed class ExecutionFleetReconciliationWorkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 22, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset StaleAt = Now.AddSeconds(-30);

    [Fact]
    public void ReconcileAvailability_PromotesApprovedReconnectedOffice()
    {
        var node = new ExecutionNode
        {
            Status = ExecutionNodeStatus.Offline,
            ApprovedAt = Now.AddHours(-1),
            LastHeartbeatAt = Now.AddSeconds(-1),
            UpdatedAt = Now.AddMinutes(-5)
        };

        var changed = ExecutionFleetReconciliationWorker.ReconcileAvailability(node, Now, StaleAt);

        Assert.True(changed);
        Assert.Equal(ExecutionNodeStatus.Ready, node.Status);
        Assert.Equal(Now, node.UpdatedAt);
    }

    [Fact]
    public void ReconcileAvailability_DemotesOfficeWithStaleHeartbeat()
    {
        var node = new ExecutionNode
        {
            Status = ExecutionNodeStatus.Ready,
            ApprovedAt = Now.AddHours(-1),
            LastHeartbeatAt = StaleAt.AddTicks(-1),
            UpdatedAt = Now.AddMinutes(-5)
        };

        var changed = ExecutionFleetReconciliationWorker.ReconcileAvailability(node, Now, StaleAt);

        Assert.True(changed);
        Assert.Equal(ExecutionNodeStatus.Offline, node.Status);
        Assert.Equal(Now, node.UpdatedAt);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void ReconcileAvailability_DoesNotPromoteIneligibleOffice(
        bool approved,
        bool draining,
        bool revoked)
    {
        var updatedAt = Now.AddMinutes(-5);
        var node = new ExecutionNode
        {
            Status = ExecutionNodeStatus.Offline,
            ApprovedAt = approved ? Now.AddHours(-1) : null,
            DrainingAt = draining ? Now.AddMinutes(-1) : null,
            RevokedAt = revoked ? Now.AddMinutes(-1) : null,
            LastHeartbeatAt = Now.AddSeconds(-1),
            UpdatedAt = updatedAt
        };

        var changed = ExecutionFleetReconciliationWorker.ReconcileAvailability(node, Now, StaleAt);

        Assert.False(changed);
        Assert.Equal(ExecutionNodeStatus.Offline, node.Status);
        Assert.Equal(updatedAt, node.UpdatedAt);
    }
}
