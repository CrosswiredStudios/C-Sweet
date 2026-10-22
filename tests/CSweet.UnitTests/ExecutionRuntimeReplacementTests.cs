using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed partial class ExecutionWorkloadOrchestratorTests
{
    [Fact]
    public async Task LostRuntimeAssignmentIsRetiredInsteadOfRebootingConsumedIdentity()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        var pool = Pool();
        var node = Node(pool, Guid.NewGuid());
        var runtime = new AgentRuntimeInstance { Id = Guid.NewGuid(), QueuedAt = Now };
        runtime.TransitionTo(AgentRuntimeStatus.Starting, Now);
        runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, Now);
        runtime.TransitionTo(AgentRuntimeStatus.Running, Now);
        var assignment = Assignment(pool.Id, node.Id, 1, 512);
        assignment.WorkloadKind = ExecutionWorkloadKind.Runtime;
        assignment.StopEvidenceVersion = 1;
        assignment.AgentBuildJobId = null;
        assignment.AgentRuntimeInstanceId = runtime.Id;
        assignment.LeaseExpiresAt = Now.AddSeconds(-1);
        var epoch = assignment.FencingEpoch;
        db.AddRange(pool, node, runtime, assignment);
        db.Add(new ExecutionAssignmentAttempt { AssignmentId = assignment.Id, FencingEpoch = epoch,
            ExecutionNodeId = node.Id, ProviderId = assignment.ProviderId, ProviderInstanceId = "old-vm", AssignedAt = Now });
        await db.SaveChangesAsync();
        var scheduler = new ExecutionWorkloadOrchestrator(db, clock);

        Assert.Equal(1, await scheduler.FenceExpiredAsync());
        Assert.Equal(ExecutionAssignmentStatus.Failed, assignment.Status);
        Assert.Equal("runtime-assignment-lease-expired", assignment.FailureCode);
        Assert.Equal(epoch + 1, assignment.FencingEpoch);
        Assert.Equal(0, await scheduler.AssignPendingAsync());
        Assert.Empty(await scheduler.GetNodeAssignmentsAsync(node.Id, node.SessionEpoch));
        Assert.False(await scheduler.RenewLeaseAsync(node.Id, assignment.Id, epoch));
        Assert.False(await scheduler.ReportStatusAsync(node.Id, assignment.Id, epoch,
            ExecutionAssignmentStatus.Running, null, null, null));
        var runner = new FleetAgentWorkloadRunner(db, scheduler);
        Assert.Equal(IsolationWorkloadState.Stopping, (await runner.InspectAsync(StopHandle(assignment.Id)))!.State);
        Assert.True(await scheduler.ReportStoppedAsync(node.Id, assignment.Id, epoch, assignment.ProviderId, "old-vm", false));
        Assert.Equal(IsolationWorkloadState.Stopped, (await runner.InspectAsync(StopHandle(assignment.Id)))!.State);
    }

    [Theory]
    [InlineData(AgentRuntimeStatus.Running)]
    [InlineData(AgentRuntimeStatus.Stopping)]
    [InlineData(AgentRuntimeStatus.Failed)]
    public async Task BrokerRejectsReplacementUsingAnAlreadyConsumedRuntime(AgentRuntimeStatus status)
    {
        await using var db = CreateDb();
        var runtime = new AgentRuntimeInstance { Id = Guid.NewGuid(), QueuedAt = Now };
        if (status == AgentRuntimeStatus.Running)
        {
            runtime.TransitionTo(AgentRuntimeStatus.Starting, Now);
            runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, Now);
        }
        runtime.TransitionTo(status, Now);
        db.Add(runtime);
        await db.SaveChangesAsync();
        var broker = StartupBroker(db, new MutableTimeProvider(Now));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            broker.WaitForRuntimeStartupAsync(runtime.Id, default));
        Assert.Contains("fresh runtime identity", error.Message);
    }

    [Fact]
    public async Task BrokerWaitsOnlyForFreshStartupAndBoundsTheWait()
    {
        await using var db = CreateDb();
        var runtime = new AgentRuntimeInstance { Id = Guid.NewGuid(), QueuedAt = Now };
        runtime.TransitionTo(AgentRuntimeStatus.Starting, Now);
        db.Add(runtime);
        await db.SaveChangesAsync();
        var clock = new MutableTimeProvider(Now);
        var broker = StartupBroker(db, clock);
        var wait = broker.WaitForRuntimeStartupAsync(runtime.Id, default);
        Assert.False(wait.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(31));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => wait);
        Assert.Contains("startup window", error.Message);
        runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, clock.GetUtcNow());
        await db.SaveChangesAsync();
        await broker.WaitForRuntimeStartupAsync(runtime.Id, default);
    }

    private static ExecutionBrokerSessionRunner StartupBroker(CSweet.Infrastructure.Persistence.CSweetDbContext db, TimeProvider clock) =>
        new(db, null!, null!, null!, null!, null!, null!, null!, null!, clock,
            NullLogger<ExecutionBrokerSessionRunner>.Instance);
}
