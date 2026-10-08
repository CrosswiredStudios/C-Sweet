using System.Data.Common;
using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Office.Contracts.Workloads;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresFact]
    public async Task FleetStartupCancellationRechecksStopProofAfterAssignmentRace()
    {
        foreach (var race in new[] { false, true })
        {
            await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
            var request = await SeedStartupAdmissionAsync(fixture);
            await using var db = fixture.Context();
            var installation = await db.AgentInstallations.SingleAsync();
            installation.ExecutionPoolId = request.ExecutionPoolId;
            var node = new ExecutionNode { Id = Guid.NewGuid(), ExecutionPoolId = request.ExecutionPoolId!.Value, Name = "race-node" };
            db.ExecutionNodes.Add(node);
            await db.SaveChangesAsync();
            using var cancellation = new CancellationTokenSource();
            var scheduler = new ExecutionWorkloadOrchestrator(db, TimeProvider.System);
            var boundary = new StartupCancellationBoundary(scheduler, cancellation, async assignmentId =>
            {
                if (!race) return;
                var assignment = await db.ExecutionWorkloadAssignments.SingleAsync(x => x.Id == assignmentId);
                assignment.Status = ExecutionAssignmentStatus.Assigned;
                assignment.ExecutionNodeId = node.Id;
                assignment.ProviderId = "test";
                assignment.FencingEpoch++;
                db.ExecutionAssignmentAttempts.Add(new ExecutionAssignmentAttempt { AssignmentId = assignmentId,
                    FencingEpoch = assignment.FencingEpoch, ExecutionNodeId = node.Id, ProviderId = "test", AssignedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
            });
            var runtime = await db.AgentRuntimeInstances.SingleAsync();
            var specification = new RuntimeWorkloadSpecification(runtime.Id,
                new("test", "1", request.GuestImageDigest, "linux", "x64"),
                new(1, 100, 512, 1024, 100, 1024, TimeSpan.FromMinutes(10)),
                new(Guid.NewGuid(), "1.0", "test-token", request.GuestImageDigest, request.GuestImageDigest, DateTimeOffset.UtcNow.AddHours(1)),
                new(request.GuestImageDigest, "test", "1", "linux", "x64"),
                new(installation.Id, installation.BusinessId, runtime.TickId), ["agent"]);
            var start = new FleetAgentWorkloadRunner(db, boundary).CreateAndStartAsync(specification,
                AgentTrustLevel.UntrustedRepository, cancellationToken: cancellation.Token);
            if (race) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
            else await Assert.ThrowsAsync<AgentWorkloadCapacityUnavailableException>(() => start);
            var retained = await db.ExecutionWorkloadAssignments.SingleAsync();
            Assert.Equal(ExecutionAssignmentStatus.Cancelled, retained.Status);
            Assert.Equal(race ? 1 : 0, await db.ExecutionAssignmentAttempts.CountAsync(x => x.StoppedAt == null));
        }
    }

    [MemoryPostgresFact]
    public async Task RuntimeStartupAdmissionCannotCrossCommittedStopOrReset()
    {
        foreach (var reset in new[] { false, true })
        {
            await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
            var request = await SeedStartupAdmissionAsync(fixture);
            await using var stopping = fixture.Context();
            await using var transaction = await stopping.Database.BeginTransactionAsync();
            var runtime = await stopping.AgentRuntimeInstances.SingleAsync();
            if (reset) runtime.MemoryResetRequestedAt = DateTimeOffset.UtcNow;
            else runtime.TransitionTo(AgentRuntimeStatus.Stopping, DateTimeOffset.UtcNow);
            await stopping.SaveChangesAsync();
            var observed = new StartupAdmissionCommandObserved("FOR UPDATE");
            await using var delayed = fixture.Context(observed);
            var submission = new ExecutionWorkloadOrchestrator(delayed, TimeProvider.System).SubmitAsync(request);
            await observed.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(submission.IsCompleted);
            await transaction.CommitAsync();
            await Assert.ThrowsAsync<AgentWorkloadException>(() => submission);
            Assert.Empty(await stopping.ExecutionWorkloadAssignments.AsNoTracking().ToListAsync());
        }
    }

    [MemoryPostgresFact]
    public async Task RuntimeStartupSubmissionThatWinsMustBeVisibleBeforeStoppingCommits()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var request = await SeedStartupAdmissionAsync(fixture);
        var pause = new StartupAdmissionCommitPause();
        await using var submitting = fixture.Context(pause);
        var submission = new ExecutionWorkloadOrchestrator(submitting, TimeProvider.System).SubmitAsync(request);
        await pause.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var observed = new StartupAdmissionCommandObserved("UPDATE");
        await using var stopping = fixture.Context(observed);
        var runtime = await stopping.AgentRuntimeInstances.SingleAsync();
        runtime.TransitionTo(AgentRuntimeStatus.Stopping, DateTimeOffset.UtcNow);
        var saveStop = stopping.SaveChangesAsync();
        try
        {
            await observed.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(saveStop.IsCompleted);
        }
        finally { pause.Release.TrySetResult(); }
        var reference = await submission;
        await saveStop;
        var retained = await stopping.ExecutionWorkloadAssignments.AsNoTracking().SingleAsync();
        Assert.Equal(reference.AssignmentId, retained.Id);
        Assert.Equal(runtime.Id, retained.AgentRuntimeInstanceId);
        var scheduler = new ExecutionWorkloadOrchestrator(stopping, TimeProvider.System);
        await scheduler.CancelAsync(retained.Id, "Recovery cancels the retained assignment");
        var handle = new IsolationWorkloadHandle("execution-fleet", runtime.Id, retained.Id.ToString("N"),
            CSweet.Office.Contracts.Workloads.WorkloadKind.Runtime);
        Assert.Equal(IsolationWorkloadState.Stopped, (await new FleetAgentWorkloadRunner(stopping, scheduler).InspectAsync(handle))!.State);
    }

    private static async Task<ExecutionWorkloadRequest> SeedStartupAdmissionAsync(DurabilityFixture fixture)
    {
        await using var db = fixture.Context();
        var runtime = new AgentRuntimeInstance { Id = Guid.NewGuid(), TickId = Guid.NewGuid(), AgentInstallationId = fixture.InstallationId };
        runtime.TransitionTo(AgentRuntimeStatus.Starting, DateTimeOffset.UtcNow);
        var pool = new ExecutionPool { Id = Guid.NewGuid(), Name = "startup-admission" };
        db.AddRange(runtime, pool);
        await db.SaveChangesAsync();
        return new(ExecutionWorkloadKind.Runtime, null, runtime.Id, pool.Id, fixture.OrganizationId.ToString("D"),
            null, "sha256:" + new string('a', 64), null, 1, 512, 1024, "{}");
    }

    private sealed class StartupAdmissionCommandObserved(string statement) : DbCommandInterceptor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("AgentRuntimeInstances", StringComparison.Ordinal) &&
                command.CommandText.Contains(statement, StringComparison.Ordinal)) Started.TrySetResult();
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class StartupAdmissionCommitPause : DbTransactionInterceptor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(25), cancellationToken);
            return result;
        }
    }

    private sealed class StartupCancellationBoundary(IExecutionWorkloadOrchestrator inner, CancellationTokenSource cancellation,
        Func<Guid, Task> beforeCancel) : IExecutionWorkloadOrchestrator
    {
        public Task<bool> HasCapacityAsync(ExecutionWorkloadRequest request, CancellationToken token = default) => Task.FromResult(true);
        public async Task<ExecutionWorkloadReference> SubmitAsync(ExecutionWorkloadRequest request, CancellationToken token = default)
        {
            var result = await inner.SubmitAsync(request, token);
            cancellation.Cancel();
            return result;
        }
        public async Task<bool> CancelAsync(Guid id, string reason, CancellationToken token = default)
        {
            await beforeCancel(id);
            return await inner.CancelAsync(id, reason, token);
        }
        public Task<int> AssignPendingAsync(CancellationToken token = default) => inner.AssignPendingAsync(token);
        public Task<IReadOnlyList<ExecutionAssignmentLease>> GetNodeAssignmentsAsync(Guid node, long epoch, CancellationToken token = default) => inner.GetNodeAssignmentsAsync(node, epoch, token);
        public Task<string?> IssueArtifactReadGrantAsync(Guid node, Guid assignment, long epoch, CancellationToken token = default) => inner.IssueArtifactReadGrantAsync(node, assignment, epoch, token);
        public Task<bool> RenewLeaseAsync(Guid node, Guid assignment, long epoch, CancellationToken token = default) => inner.RenewLeaseAsync(node, assignment, epoch, token);
        public Task<bool> ReportStatusAsync(Guid node, Guid assignment, long epoch, ExecutionAssignmentStatus status, string? code, string? failure, ExecutionWorkloadResult? result, CancellationToken token = default) => inner.ReportStatusAsync(node, assignment, epoch, status, code, failure, result, token);
        public Task<int> FenceExpiredAsync(CancellationToken token = default) => inner.FenceExpiredAsync(token);
        public Task<bool> ReportStoppedAsync(Guid node, Guid assignment, long epoch, string provider, string instance, bool neverCreated, CancellationToken token = default) => inner.ReportStoppedAsync(node, assignment, epoch, provider, instance, neverCreated, token);
    }
}
