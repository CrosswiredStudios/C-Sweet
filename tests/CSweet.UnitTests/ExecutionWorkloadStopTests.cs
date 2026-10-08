using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class ExecutionWorkloadOrchestratorTests
{
    [Fact]
    public async Task CancellationRequiresBoundStopReportAndDuplicateIsIdempotent()
    {
        await using var db = CreateDb();
        var pool = Pool();
        var node = Node(pool, Guid.NewGuid());
        db.AddRange(pool, node);
        await db.SaveChangesAsync();
        var clock = new MutableTimeProvider(Now);
        var scheduler = new ExecutionWorkloadOrchestrator(db, clock);
        var reference = await scheduler.SubmitAsync(Request(Guid.NewGuid(), pool.Id));
        Assert.Equal(1, await scheduler.AssignPendingAsync());
        var execution = Assert.Single(db.ExecutionAssignmentAttempts);
        Assert.True(await scheduler.ReportStatusAsync(node.Id, reference.AssignmentId, execution.FencingEpoch,
            ExecutionAssignmentStatus.Starting, null, null, null));
        Assert.True(await scheduler.ReportStatusAsync(node.Id, reference.AssignmentId, execution.FencingEpoch,
            ExecutionAssignmentStatus.Running, null, null, new("vm-1", null)));
        Assert.True(await scheduler.CancelAsync(reference.AssignmentId, "test"));
        var runner = new FleetAgentWorkloadRunner(db, scheduler);
        var handle = StopHandle(reference.AssignmentId);
        Assert.Equal(IsolationWorkloadState.Stopping, (await runner.InspectAsync(handle))!.State);
        Assert.False(await scheduler.ReportStoppedAsync(Guid.NewGuid(), reference.AssignmentId, execution.FencingEpoch,
            execution.ProviderId, "vm-1", false));
        Assert.False(await scheduler.ReportStoppedAsync(node.Id, reference.AssignmentId, execution.FencingEpoch + 1,
            execution.ProviderId, "vm-1", false));
        Assert.False(await scheduler.ReportStoppedAsync(node.Id, reference.AssignmentId, execution.FencingEpoch,
            "wrong-provider", "vm-1", false));
        Assert.False(await scheduler.ReportStoppedAsync(node.Id, reference.AssignmentId, execution.FencingEpoch,
            execution.ProviderId, "wrong-vm", false));
        Assert.False(await scheduler.ReportStoppedAsync(node.Id, reference.AssignmentId, execution.FencingEpoch,
            execution.ProviderId, "", true));
        Assert.True(await scheduler.ReportStoppedAsync(node.Id, reference.AssignmentId, execution.FencingEpoch,
            execution.ProviderId, "vm-1", false));
        var stoppedAt = execution.StoppedAt;
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await scheduler.ReportStoppedAsync(node.Id, reference.AssignmentId, execution.FencingEpoch,
            execution.ProviderId, "vm-1", false));
        Assert.Equal(stoppedAt, execution.StoppedAt);
        Assert.Equal(IsolationWorkloadState.Stopped, (await runner.InspectAsync(handle))!.State);
        Assert.Equal(ExecutionAssignmentStatus.Cancelled, (await db.ExecutionWorkloadAssignments.SingleAsync()).Status);
    }

    [Fact]
    public async Task LatestStopDoesNotHideEarlierUnconfirmedAttempt()
    {
        await using var db = CreateDb();
        var pool = Pool();
        var node = Node(pool, Guid.NewGuid());
        db.AddRange(pool, node);
        await db.SaveChangesAsync();
        var clock = new MutableTimeProvider(Now);
        var scheduler = new ExecutionWorkloadOrchestrator(db, clock);
        var reference = await scheduler.SubmitAsync(Request(Guid.NewGuid(), pool.Id));
        await scheduler.AssignPendingAsync();
        var first = Assert.Single(db.ExecutionAssignmentAttempts);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await scheduler.FenceExpiredAsync());
        node.LastHeartbeatAt = clock.GetUtcNow();
        await db.SaveChangesAsync();
        Assert.Equal(1, await scheduler.AssignPendingAsync());
        var second = await db.ExecutionAssignmentAttempts.SingleAsync(x => x.FencingEpoch != first.FencingEpoch);
        await scheduler.CancelAsync(reference.AssignmentId, "test");
        Assert.True(await scheduler.ReportStoppedAsync(node.Id, reference.AssignmentId, second.FencingEpoch,
            second.ProviderId, "vm-2", false));
        var runner = new FleetAgentWorkloadRunner(db, scheduler);
        Assert.Equal(IsolationWorkloadState.Stopping, (await runner.InspectAsync(StopHandle(reference.AssignmentId)))!.State);
        Assert.True(await scheduler.ReportStoppedAsync(node.Id, reference.AssignmentId, first.FencingEpoch,
            first.ProviderId, "vm-1", false));
        Assert.Equal(IsolationWorkloadState.Stopped, (await runner.InspectAsync(StopHandle(reference.AssignmentId)))!.State);
    }

    [Fact]
    public async Task StopWithoutTerminalStatusFencesActiveAttemptAndPreventsRenewal()
    {
        await using var db = CreateDb();
        var pool = Pool();
        var node = Node(pool, Guid.NewGuid());
        db.AddRange(pool, node);
        await db.SaveChangesAsync();
        var scheduler = new ExecutionWorkloadOrchestrator(db, new MutableTimeProvider(Now));
        var reference = await scheduler.SubmitAsync(Request(Guid.NewGuid(), pool.Id));
        await scheduler.AssignPendingAsync();
        var execution = Assert.Single(db.ExecutionAssignmentAttempts);
        Assert.True(await scheduler.ReportStoppedAsync(node.Id, reference.AssignmentId, execution.FencingEpoch,
            execution.ProviderId, "", true));
        Assert.Empty(await scheduler.GetNodeAssignmentsAsync(node.Id, node.SessionEpoch));
        Assert.False(await scheduler.RenewLeaseAsync(node.Id, reference.AssignmentId, execution.FencingEpoch));
        Assert.False(await scheduler.ReportStatusAsync(node.Id, reference.AssignmentId, execution.FencingEpoch,
            ExecutionAssignmentStatus.Running, null, null, null));
        Assert.Equal("office-workload-stopped", (await db.ExecutionWorkloadAssignments.SingleAsync()).FailureCode);
        Assert.False(await scheduler.ReportStoppedAsync(node.Id, reference.AssignmentId, execution.FencingEpoch,
            execution.ProviderId, "vm-1", false));
    }

    [Theory]
    [InlineData(0, IsolationWorkloadState.Stopping)]
    [InlineData(1, IsolationWorkloadState.Stopped)]
    public async Task NeverAssignedCancellationRequiresKnownAttemptHistory(int version, IsolationWorkloadState expected)
    {
        await using var db = CreateDb();
        var pool = Pool();
        db.Add(pool);
        await db.SaveChangesAsync();
        var scheduler = new ExecutionWorkloadOrchestrator(db, new MutableTimeProvider(Now));
        var reference = await scheduler.SubmitAsync(Request(Guid.NewGuid(), pool.Id));
        (await db.ExecutionWorkloadAssignments.SingleAsync()).StopEvidenceVersion = version;
        await scheduler.CancelAsync(reference.AssignmentId, "test");
        Assert.Equal(expected, (await new FleetAgentWorkloadRunner(db, scheduler)
            .InspectAsync(StopHandle(reference.AssignmentId)))!.State);
    }

    private static IsolationWorkloadHandle StopHandle(Guid assignmentId) =>
        new("execution-fleet", Guid.NewGuid(), assignmentId.ToString("N"), CSweet.Office.Contracts.Workloads.WorkloadKind.Runtime);
}
