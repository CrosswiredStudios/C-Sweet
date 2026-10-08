using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Office.Contracts.Workloads;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentRuntimeManagerTests
{
    [Theory]
    [InlineData("failure")]
    [InlineData("timeout")]
    [InlineData("capacity")]
    public async Task FailedStartupRetainsSlotUntilEveryFleetAttemptIsStopped(string failure)
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        installation.Schedule!.ActivationMode = ActivationMode.AlwaysOn;
        await db.SaveChangesAsync();
        var assignmentId = Guid.NewGuid();
        var runner = new FakeRunner
        {
            ConfirmStop = false,
            StartException = failure switch
            {
                "timeout" => new OperationCanceledException(),
                "capacity" => new AgentWorkloadCapacityUnavailableException("Capacity changed during assignment."),
                _ => new AgentWorkloadException("Office startup failed after assignment.")
            },
            BeforeStart = async specification =>
            {
                var runtime = await db.AgentRuntimeInstances.SingleAsync(x => x.Id == specification.WorkloadId);
                db.ExecutionWorkloadAssignments.Add(new ExecutionWorkloadAssignment { Id = assignmentId,
                    AgentRuntimeInstanceId = runtime.Id, WorkloadKind = ExecutionWorkloadKind.Runtime,
                    Status = ExecutionAssignmentStatus.Cancelled });
                db.McpAgentSessions.Add(SessionFor(runtime, installation, DateTimeOffset.UtcNow.AddMinutes(5)));
                await db.SaveChangesAsync();
            }
        };
        var manager = CreateManager(db, runner);
        await manager.EnsureRuntimeQueuedAsync(installation.Id, "Start recovery test");
        await manager.ReconcileAsync();
        var failed = await db.AgentRuntimeInstances.SingleAsync();
        Assert.Equal(AgentRuntimeStatus.Stopping, failed.Status);
        Assert.Null(failed.CompletedAt);
        Assert.NotNull((await db.McpAgentSessions.SingleAsync()).RevokedAt);
        Assert.Contains(assignmentId.ToString("N"), runner.Stops);
        Assert.Equal(0, installation.Schedule.ConsecutiveStartupFailures);
        Assert.False(await manager.EnsureRuntimeQueuedAsync(installation.Id, "Must not overlap"));
        var stopping = await db.AgentRuntimeEvents.SingleAsync(x => x.AgentRuntimeInstanceId == failed.Id && x.Status == AgentRuntimeStatus.Stopping);
        stopping.OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        runner.ConfirmStop = true;
        await manager.ReconcileAsync();
        Assert.Equal(AgentRuntimeStatus.StartFailed, failed.Status);
        Assert.Equal(1, installation.Schedule.ConsecutiveStartupFailures);
        Assert.True(await manager.EnsureRuntimeQueuedAsync(installation.Id, "Confirmed clean retry"));
    }

    [Theory]
    [InlineData("no-handle")]
    [InlineData("inspect")]
    [InlineData("unconfirmed")]
    public async Task InterruptedStartupCannotReleaseSlotOnUnknownCleanup(string failure)
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        installation.Schedule!.ActivationMode = ActivationMode.OnDemand;
        installation.Schedule.NextTickAt = null;
        var runtime = new AgentRuntimeInstance { Id = Guid.NewGuid(), TickId = Guid.NewGuid(),
            AgentInstallationId = installation.Id, QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
        runtime.TransitionTo(AgentRuntimeStatus.Starting, DateTimeOffset.UtcNow.AddMinutes(-5));
        var assignmentId = Guid.NewGuid();
        if (failure == "no-handle")
            db.ExecutionWorkloadAssignments.Add(new ExecutionWorkloadAssignment { Id = assignmentId,
                AgentRuntimeInstanceId = runtime.Id, WorkloadKind = ExecutionWorkloadKind.Runtime });
        else
        {
            runtime.IsolationProviderId = "test-vm";
            runtime.ProviderInstanceId = "partial-vm";
        }
        db.AgentRuntimeInstances.Add(runtime);
        await db.SaveChangesAsync();
        var runner = new FakeRunner { ConfirmStop = false, ConfirmDestroy = false,
            InspectException = failure == "inspect" ? new AgentWorkloadException("Provider unavailable") : null,
            InspectStatus = new(new("test-vm", runtime.Id, "partial-vm", WorkloadKind.Runtime),
                IsolationWorkloadState.Failed, IsolationTerminationReason.ProviderFailure, 1, null, null, null, null) };
        var manager = CreateManager(db, runner);
        await manager.ReconcileAsync();
        Assert.Equal(AgentRuntimeStatus.Stopping, runtime.Status);
        Assert.Null(runtime.CompletedAt);
        Assert.False(await manager.EnsureRuntimeQueuedAsync(installation.Id, "Unsafe replacement"));
        var stopping = await db.AgentRuntimeEvents.SingleAsync(x => x.AgentRuntimeInstanceId == runtime.Id && x.Status == AgentRuntimeStatus.Stopping);
        stopping.OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        runner.InspectException = null;
        await manager.ReconcileAsync();
        Assert.Equal(AgentRuntimeStatus.Stopping, runtime.Status);
        runner.ConfirmStop = true;
        runner.ConfirmDestroy = true;
        await manager.ReconcileAsync();
        Assert.Equal(AgentRuntimeStatus.StartFailed, runtime.Status);
        Assert.True(await manager.EnsureRuntimeQueuedAsync(installation.Id, "Safe replacement"));
    }
}
