using System.Data.Common;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresFact]
    public async Task FleetStopProofAndFencingRollBackTogetherBeforeRetry()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (assignmentId, nodeId) = await SeedFleetStopAsync(fixture);
        var failure = new StopEvidenceCommitFailure();
        await using (var db = fixture.Context(failure))
            await Assert.ThrowsAsync<IOException>(() => new ExecutionWorkloadOrchestrator(db, TimeProvider.System)
                .ReportStoppedAsync(nodeId, assignmentId, 2, "test", "vm-1", false));
        Assert.True(failure.Observed);
        await using (var fresh = fixture.Context())
        {
            var assignment = await fresh.ExecutionWorkloadAssignments.SingleAsync();
            Assert.Equal(ExecutionAssignmentStatus.Running, assignment.Status);
            Assert.Equal(2, assignment.FencingEpoch);
            Assert.Null((await fresh.ExecutionAssignmentAttempts.SingleAsync()).StoppedAt);
            Assert.True(await new ExecutionWorkloadOrchestrator(fresh, TimeProvider.System)
                .ReportStoppedAsync(nodeId, assignmentId, 2, "test", "vm-1", false));
        }
        await using (var fresh = fixture.Context())
        {
            var assignment = await fresh.ExecutionWorkloadAssignments.SingleAsync();
            Assert.Equal(ExecutionAssignmentStatus.Failed, assignment.Status);
            Assert.Equal(3, assignment.FencingEpoch);
            Assert.Null(assignment.LeaseExpiresAt);
            var stopped = (await fresh.ExecutionAssignmentAttempts.SingleAsync()).StoppedAt;
            Assert.NotNull(stopped);
            Assert.True(await new ExecutionWorkloadOrchestrator(fresh, TimeProvider.System)
                .ReportStoppedAsync(nodeId, assignmentId, 2, "test", "vm-1", false));
            Assert.Equal(stopped, (await fresh.ExecutionAssignmentAttempts.SingleAsync()).StoppedAt);
        }
    }

    [MemoryPostgresFact]
    public async Task CompetingFleetStopReportsCannotReplacePersistedInstanceProof()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (assignmentId, nodeId) = await SeedFleetStopAsync(fixture);
        await using var first = fixture.Context();
        await using var second = fixture.Context();
        var results = await Task.WhenAll(
            new ExecutionWorkloadOrchestrator(first, TimeProvider.System).ReportStoppedAsync(nodeId, assignmentId, 2, "test", "vm-1", false),
            new ExecutionWorkloadOrchestrator(second, TimeProvider.System).ReportStoppedAsync(nodeId, assignmentId, 2, "test", "vm-2", false));
        Assert.Single(results, accepted => accepted);
        await using var fresh = fixture.Context();
        Assert.Equal(results[0] ? "vm-1" : "vm-2", (await fresh.ExecutionAssignmentAttempts.SingleAsync()).ProviderInstanceId);
        Assert.Equal(3, (await fresh.ExecutionWorkloadAssignments.SingleAsync()).FencingEpoch);
    }

    private static async Task<(Guid AssignmentId, Guid NodeId)> SeedFleetStopAsync(DurabilityFixture fixture)
    {
        await using var db = fixture.Context();
        var now = DateTimeOffset.UtcNow;
        var pool = new ExecutionPool { Id = Guid.NewGuid(), Name = "stop-test" };
        var node = new ExecutionNode { Id = Guid.NewGuid(), ExecutionPoolId = pool.Id, Name = "test" };
        var runtime = new AgentRuntimeInstance { Id = Guid.NewGuid(), AgentInstallationId = fixture.InstallationId,
            TickId = Guid.NewGuid(), QueuedAt = now, RuntimeDeadlineAt = now.AddHours(1) };
        var assignment = new ExecutionWorkloadAssignment
        {
            Id = Guid.NewGuid(), ExecutionPoolId = pool.Id, ExecutionNodeId = node.Id,
            AgentRuntimeInstanceId = runtime.Id, WorkloadKind = ExecutionWorkloadKind.Runtime,
            Status = ExecutionAssignmentStatus.Running, ProviderId = "test", FencingEpoch = 2,
            StopEvidenceVersion = 1, QueuedAt = now, AssignedAt = now, LeaseExpiresAt = now.AddMinutes(1)
        };
        db.AddRange(pool, node, runtime, assignment, new ExecutionAssignmentAttempt
        {
            AssignmentId = assignment.Id, FencingEpoch = 2, ExecutionNodeId = node.Id,
            ProviderId = "test", AssignedAt = now
        });
        await db.SaveChangesAsync();
        return (assignment.Id, node.Id);
    }

    private sealed class StopEvidenceCommitFailure : DbTransactionInterceptor
    {
        public bool Observed { get; private set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Observed = true;
            throw new IOException("injected_stop_commit_failure");
        }
    }
}
