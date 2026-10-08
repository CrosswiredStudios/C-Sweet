using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Office.Contracts.Workloads;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentRuntimeManagerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableMemoryResetSettlesDeliveredWorkBeforeConfirmedReplacement(bool manualRestart)
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        installation.Schedule!.ActivationMode = ActivationMode.OnDemand;
        installation.Schedule.NextTickAt = null;
        var runtime = RunningInstance(installation.Id);
        runtime.ProviderInstanceId = "reset-vm";
        runtime.AgentInstallation = installation;
        db.AddRange(runtime, SessionFor(runtime, installation, DateTimeOffset.UtcNow.AddMinutes(5)));
        await db.SaveChangesAsync();
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var delivered = await inbox.EnqueueAsync(installation.BusinessId, installation.Id, AgentWorkKind.Capability,
            "delivered", JsonSerializer.SerializeToElement(new { }), "delivered", DateTimeOffset.UtcNow.AddHours(1));
        delivered.Status = AgentWorkStatus.Leased;
        delivered.AttemptCount = 1;
        db.AgentWorkAttempts.Add(new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = delivered.Id,
            RuntimeInstanceId = runtime.Id, Attempt = 1, ClaimedAt = DateTimeOffset.UtcNow,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5), LeaseTokenHash = "lease" });
        await db.SaveChangesAsync();
        var pending = await inbox.EnqueueAsync(installation.BusinessId, installation.Id, AgentWorkKind.Capability,
            "pending", JsonSerializer.SerializeToElement(new { }), "pending", DateTimeOffset.UtcNow.AddHours(1));
        Assert.True(await new AgentMemoryRuntimeReset(db).RequestAsync(runtime.Id, runtime.TickId, installation.Id,
            installation.BusinessId, installation.Grant!.GrantRevision, MemoryRuntimeResetRequiredException.RetainedEvidence, default));
        var runner = new FakeRunner { ConfirmStop = false, ConfirmDestroy = false };
        var manager = CreateManager(db, runner, workInbox: inbox);
        if (manualRestart) Assert.False(await manager.RestartRuntimeAsync(installation.Id, "Operator restart"));
        else await manager.ReconcileAsync();
        Assert.Equal(AgentWorkStatus.DeadLetter, (await inbox.ReadStateAsync(delivered.Id, default)).Status);
        Assert.Equal(MemoryRuntimeResetRequiredException.FailureCode, (await inbox.ReadStateAsync(delivered.Id, default)).Completion?.FailureCode);
        Assert.Equal(AgentWorkStatus.Pending, pending.Status);
        Assert.Equal(AgentRuntimeStatus.Stopping, runtime.Status);
        Assert.Null(runtime.MemoryResetCompletedAt);
        Assert.Equal(0, await manager.EnsurePendingOnDemandRuntimesAsync());
        var stopping = await db.AgentRuntimeEvents.SingleAsync(x => x.AgentRuntimeInstanceId == runtime.Id && x.Status == AgentRuntimeStatus.Stopping);
        stopping.OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        runner.ConfirmDestroy = true;
        await manager.ReconcileAsync();
        Assert.Equal(AgentRuntimeStatus.Cancelled, runtime.Status);
        Assert.NotNull(runtime.MemoryResetCompletedAt);
        Assert.Equal(1, await manager.EnsurePendingOnDemandRuntimesAsync());
        Assert.NotEqual(runtime.Id, (await db.AgentRuntimeInstances.SingleAsync(x => x.Status == AgentRuntimeStatus.Queued)).Id);
    }

    [Fact]
    public async Task DurableMemoryResetWaitsForFleetAssignmentEvenWhenRuntimeHandleWasNotSaved()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        installation.Schedule!.ActivationMode = ActivationMode.OnDemand;
        installation.Schedule.NextTickAt = null;
        var runtime = RunningInstance(installation.Id);
        runtime.AgentInstallation = installation;
        var assignment = new ExecutionWorkloadAssignment { Id = Guid.NewGuid(), AgentRuntimeInstanceId = runtime.Id,
            WorkloadKind = ExecutionWorkloadKind.Runtime, Status = ExecutionAssignmentStatus.Cancelled };
        db.AddRange(runtime, assignment);
        await db.SaveChangesAsync();
        Assert.Null(runtime.ProviderInstanceId);
        Assert.True(await new AgentMemoryRuntimeReset(db).RequestAsync(runtime.Id, runtime.TickId, installation.Id,
            installation.BusinessId, installation.Grant!.GrantRevision, MemoryRuntimeResetRequiredException.LegacyEvidence, default));
        var runner = new FakeRunner { ConfirmStop = false };
        var manager = CreateManager(db, runner, workInbox: new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System));
        await manager.ReconcileAsync();
        Assert.Equal(AgentRuntimeStatus.Stopping, runtime.Status);
        Assert.Null(runtime.MemoryResetCompletedAt);
        Assert.Contains(assignment.Id.ToString("N"), runner.Stops);
        var stopping = await db.AgentRuntimeEvents.SingleAsync(x => x.AgentRuntimeInstanceId == runtime.Id && x.Status == AgentRuntimeStatus.Stopping);
        stopping.OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        runner.ConfirmStop = true;
        await manager.ReconcileAsync();
        Assert.Equal(AgentRuntimeStatus.Cancelled, runtime.Status);
        Assert.NotNull(runtime.MemoryResetCompletedAt);
    }

    [Fact]
    public async Task MemoryRecoveryFailedStopFencesSessionAndKeepsRuntimeSlotUntilRecovery()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        installation.Schedule!.ActivationMode = ActivationMode.OnDemand;
        installation.Schedule.NextTickAt = null;
        var runtime = RunningInstance(installation.Id); runtime.ProviderInstanceId = "retained-memory-vm";
        runtime.AgentInstallation = installation;
        var session = SessionFor(runtime, installation, DateTimeOffset.UtcNow.AddMinutes(5));
        db.AddRange(runtime, session);
        db.AgentMemoryReadReceipts.Add(new AgentMemoryReadReceipt { Id = Guid.NewGuid(), RuntimeId = runtime.Id,
            InstallationId = installation.Id, EvidenceJson = "retained evidence", ReceiptHash = new string('a', 64) });
        await db.SaveChangesAsync();
        var runner = new FakeRunner { StopException = new AgentWorkloadException("provider unavailable") };
        var manager = CreateManager(db, runner);

        Assert.False(await manager.RestartRuntimeAsync(installation.Id, "Memory context must be replaced.", interactive: true));
        Assert.Equal(AgentRuntimeStatus.Stopping, runtime.Status); Assert.Null(runtime.CompletedAt);
        Assert.NotNull(session.RevokedAt); Assert.Equal("retained-memory-vm", runtime.ProviderInstanceId);
        Assert.Single(await db.AgentRuntimeInstances.ToListAsync()); Assert.Single(await db.AgentMemoryReadReceipts.ToListAsync());
        var stopping = Assert.Single(await db.AgentRuntimeEvents.Where(x => x.AgentRuntimeInstanceId == runtime.Id && x.Status == AgentRuntimeStatus.Stopping).ToListAsync());
        var firstRequestedAt = stopping.OccurredAt;
        Assert.False(await manager.RestartRuntimeAsync(installation.Id, "Repeated reset request.", interactive: true));
        Assert.Single(runner.Stops); Assert.Equal(firstRequestedAt, stopping.OccurredAt);

        stopping.OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-1); await db.SaveChangesAsync();
        runner.StopException = null;
        await manager.ReconcileAsync();
        Assert.Equal(AgentRuntimeStatus.Failed, runtime.Status);
        Assert.True(await manager.EnsureRuntimeQueuedAsync(installation.Id, "Retry after confirmed recovery.", interactive: true));
        var replacement = await db.AgentRuntimeInstances.SingleAsync(x => x.Status == AgentRuntimeStatus.Queued);
        Assert.NotEqual(runtime.Id, replacement.Id); Assert.Equal(AgentRuntimeInstance.CurrentMemoryReadEvidenceVersion, replacement.MemoryReadEvidenceVersion);
        Assert.Single(await db.AgentMemoryReadReceipts.Where(x => x.RuntimeId == runtime.Id).ToListAsync());
        Assert.Empty(await db.AgentMemoryReadReceipts.Where(x => x.RuntimeId == replacement.Id).ToListAsync());
    }

    [Theory]
    [InlineData(IsolationWorkloadState.Running)]
    [InlineData(IsolationWorkloadState.Stopping)]
    [InlineData(IsolationWorkloadState.Failed)]
    public async Task MemoryRecoveryStopRequestWithoutStoppedStateCannotReplaceRuntime(IsolationWorkloadState state)
    {
        await using var db = CreateDb(); var installation = await SeedAsync(db, due: false);
        installation.Schedule!.ActivationMode = ActivationMode.OnDemand;
        installation.Schedule.NextTickAt = null;
        var runtime = RunningInstance(installation.Id); runtime.ProviderInstanceId = "unconfirmed-vm"; runtime.AgentInstallation = installation;
        db.AddRange(runtime, SessionFor(runtime, installation, DateTimeOffset.UtcNow.AddMinutes(5))); await db.SaveChangesAsync();
        var runner = new FakeRunner { ConfirmStop = false, ConfirmDestroy = false,
            InspectStatus = new(new("test-vm", runtime.Id, "unconfirmed-vm", WorkloadKind.Runtime), state,
                IsolationTerminationReason.None, null, null, null, null, null) };
        var manager = CreateManager(db, runner);
        Assert.False(await manager.RestartRuntimeAsync(installation.Id, "Replace retained context."));
        Assert.Equal(AgentRuntimeStatus.Stopping, runtime.Status); Assert.Empty(runner.Removes);
        var stopping = await db.AgentRuntimeEvents.SingleAsync(x => x.AgentRuntimeInstanceId == runtime.Id && x.Status == AgentRuntimeStatus.Stopping);
        stopping.OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-1); await db.SaveChangesAsync();
        await manager.ReconcileAsync();
        Assert.Equal(AgentRuntimeStatus.Stopping, runtime.Status); Assert.Null(runtime.CompletedAt);
        Assert.False(await manager.EnsureRuntimeQueuedAsync(installation.Id, "Must remain fenced."));
        Assert.Single(await db.AgentRuntimeInstances.ToListAsync());
        runner.ConfirmDestroy = true;
        await manager.ReconcileAsync();
        Assert.Equal(AgentRuntimeStatus.Failed, runtime.Status);
        Assert.True(await manager.EnsureRuntimeQueuedAsync(installation.Id, "Confirmed replacement."));
    }

    [Theory]
    [InlineData("destroy")]
    [InlineData("inspect")]
    public async Task MemoryRecoveryInterruptedStopFailureRetainsHandleAndDoesNotBlockOtherInstallations(string failure)
    {
        await using var db = CreateDb(); var installation = await SeedAsync(db, due: false);
        var runtime = RunningInstance(installation.Id); runtime.ProviderInstanceId = "recovering-vm";
        var stoppingAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        runtime.TransitionTo(AgentRuntimeStatus.Stopping, stoppingAt, "Memory replacement pending.");
        runtime.Events.Add(new AgentRuntimeEvent { Id = Guid.NewGuid(), AgentRuntimeInstanceId = runtime.Id,
            Status = AgentRuntimeStatus.Stopping, OccurredAt = stoppingAt });
        var session = SessionFor(runtime, installation, DateTimeOffset.UtcNow.AddMinutes(5));
        var other = await SeedAsync(db, "another-business", due: false);
        var queued = new AgentRuntimeInstance { Id = Guid.NewGuid(), TickId = Guid.NewGuid(), AgentInstallationId = other.Id, QueuedAt = DateTimeOffset.UtcNow };
        db.AddRange(runtime, session, queued); await db.SaveChangesAsync();
        var runner = new FakeRunner
        {
            DestroyException = failure == "destroy" ? new AgentWorkloadException("destroy unavailable") : null,
            InspectException = failure == "inspect" ? new AgentWorkloadException("inspect unavailable") : null
        };
        await CreateManager(db, runner).ReconcileAsync();
        Assert.Equal(AgentRuntimeStatus.Stopping, runtime.Status); Assert.Equal("recovering-vm", runtime.ProviderInstanceId);
        Assert.Null(runtime.CompletedAt); Assert.NotNull(session.RevokedAt);
        Assert.Equal(AgentRuntimeStatus.WaitingForMcpSession, queued.Status); Assert.Single(runner.Starts);
    }
}
