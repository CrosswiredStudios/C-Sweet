using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentRuntimeManagerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapacityPressure_HandsOffWarmIdleSlotOnlyAfterConfirmedShutdown(bool businessLimit)
    {
        await using var db = CreateDb();
        var (donor, waiting, work) = await SeedCapacityPressureAsync(db, businessLimit);
        var session = await db.McpAgentSessions.SingleAsync();
        FakeRunner? runner = null;
        runner = new FakeRunner
        {
            BeforeStart = _ =>
            {
                Assert.Equal(AgentRuntimeStatus.Cancelled, donor.Status);
                Assert.NotNull(session.RevokedAt);
                Assert.Contains("idle-vm", runner!.Stops);
                return Task.CompletedTask;
            }
        };
        var manager = CreateManager(db, runner);

        await manager.ReconcileAsync();
        await manager.ReconcileAsync();

        Assert.Equal(AgentRuntimeStatus.Cancelled, donor.Status);
        Assert.Contains("waiting agent capacity", donor.Reason);
        Assert.Equal(AgentRuntimeStatus.WaitingForMcpSession, waiting.Status);
        Assert.Equal(AgentWorkStatus.Pending, work.Status);
        Assert.Equal(0, work.AttemptCount);
        Assert.Single(runner.Starts);
        Assert.Single(runner.Stops);
        Assert.Single(runner.Removes);
        Assert.True(donor.IdleDeadlineAt > donor.CompletedAt);
    }

    [Fact]
    public async Task SpareCapacity_KeepsIdleRuntimeWarmWhileStartingWaitingAgent()
    {
        await using var db = CreateDb();
        var (donor, waiting, _) = await SeedCapacityPressureAsync(db);
        (await db.AgentRuntimeGlobalSettings.SingleAsync()).GlobalMaxActiveWorkloads = 2;
        await db.SaveChangesAsync();
        var runner = new FakeRunner();

        await CreateManager(db, runner).ReconcileAsync();

        Assert.Equal(AgentRuntimeStatus.Running, donor.Status);
        Assert.Equal(AgentRuntimeStatus.WaitingForMcpSession, waiting.Status);
        Assert.Empty(runner.Stops);
    }

    [Theory]
    [InlineData(AgentWorkStatus.Pending, 0, false)]
    [InlineData(AgentWorkStatus.Leased, 0, false)]
    [InlineData(AgentWorkStatus.Leased, -10, false)]
    [InlineData(AgentWorkStatus.Pending, 10, true)]
    [InlineData(AgentWorkStatus.Pending, -10, true)]
    public async Task CapacityRelease_PreservesRunnableAndLeasedWorkButYieldsDuringDelayedOrExpiredWork(
        AgentWorkStatus status, int timingMinutes, bool shouldRelease)
    {
        await using var db = CreateDb();
        var (donor, _, _) = await SeedCapacityPressureAsync(db);
        var ownWork = CapacityWork(donor.AgentInstallationId);
        ownWork.Status = status;
        if (timingMinutes > 0) ownWork.AvailableAt = DateTimeOffset.UtcNow.AddMinutes(timingMinutes);
        if (timingMinutes < 0) ownWork.DeadlineAt = DateTimeOffset.UtcNow.AddMinutes(timingMinutes);
        db.Add(ownWork);
        await db.SaveChangesAsync();
        var runner = new FakeRunner();

        await CreateManager(db, runner).ReconcileAsync();

        Assert.Equal(shouldRelease ? AgentRuntimeStatus.Cancelled : AgentRuntimeStatus.Running, donor.Status);
        Assert.Equal(status, ownWork.Status);
        Assert.Equal(0, ownWork.AttemptCount);
        Assert.Equal(shouldRelease ? 1 : 0, runner.Stops.Count);
    }

    [Theory]
    [InlineData(ActivationMode.AlwaysOn)]
    [InlineData(ActivationMode.Scheduled)]
    public async Task CapacityRelease_DoesNotEvictOtherActivationModes(ActivationMode mode)
    {
        await using var db = CreateDb();
        var (donor, waiting, _) = await SeedCapacityPressureAsync(db);
        donor.AgentInstallation!.Schedule!.ActivationMode = mode;
        await db.SaveChangesAsync();
        var runner = new FakeRunner();

        await CreateManager(db, runner).ReconcileAsync();

        Assert.Equal(AgentRuntimeStatus.Running, donor.Status);
        Assert.Equal(AgentRuntimeStatus.Queued, waiting.Status);
        Assert.Empty(runner.Stops);
    }

    [Fact]
    public async Task CapacityRelease_WithoutRunnableDemandDoesNotEvictWarmRuntime()
    {
        await using var db = CreateDb();
        var (donor, waiting, work) = await SeedCapacityPressureAsync(db);
        work.AvailableAt = DateTimeOffset.UtcNow.AddHours(1);
        await db.SaveChangesAsync();
        var runner = new FakeRunner();

        await CreateManager(db, runner).ReconcileAsync();

        Assert.Equal(AgentRuntimeStatus.Running, donor.Status);
        Assert.Equal(AgentRuntimeStatus.Queued, waiting.Status);
        Assert.Empty(runner.Stops);
    }

    [Fact]
    public async Task BusinessPressure_DoesNotEvictAnotherBusiness()
    {
        await using var db = CreateDb();
        var (donor, waiting, _) = await SeedCapacityPressureAsync(db, businessLimit: true);
        var unrelated = await SeedAsync(db, "other-business", due: false);
        unrelated.Schedule!.ActivationMode = ActivationMode.OnDemand;
        var otherRuntime = RunningInstance(unrelated.Id);
        otherRuntime.QueuedAt = donor.QueuedAt.AddMinutes(-1);
        db.AddRange(otherRuntime, SessionFor(otherRuntime, unrelated, DateTimeOffset.UtcNow.AddMinutes(10)));
        await db.SaveChangesAsync();

        await CreateManager(db, new FakeRunner()).ReconcileAsync();

        Assert.Equal(AgentRuntimeStatus.Running, otherRuntime.Status);
        Assert.Equal(AgentRuntimeStatus.Cancelled, donor.Status);
        Assert.Equal(AgentRuntimeStatus.Queued, waiting.Status);
    }

    [Fact]
    public async Task UnconfirmedCapacityRelease_RetainsSlotAndDoesNotDrainAnotherDonorOrRepeatStop()
    {
        await using var db = CreateDb();
        var (donor, waiting, _) = await SeedCapacityPressureAsync(db);
        var other = await SeedAsync(db, due: false);
        other.Schedule!.ActivationMode = ActivationMode.OnDemand;
        var otherRuntime = RunningInstance(other.Id);
        otherRuntime.ProviderInstanceId = "other-vm";
        otherRuntime.IdleDeadlineAt = donor.IdleDeadlineAt!.Value.AddMinutes(1);
        db.AddRange(otherRuntime, SessionFor(otherRuntime, other, DateTimeOffset.UtcNow.AddMinutes(10)));
        (await db.AgentRuntimeGlobalSettings.SingleAsync()).GlobalMaxActiveWorkloads = 2;
        await db.SaveChangesAsync();
        var runner = new FakeRunner { ConfirmStop = false, ConfirmDestroy = false };
        var manager = CreateManager(db, runner);

        await manager.ReconcileAsync();
        await manager.ReconcileAsync();

        Assert.Equal(AgentRuntimeStatus.Stopping, donor.Status);
        Assert.Null(donor.CompletedAt);
        Assert.Equal(AgentRuntimeStatus.Running, otherRuntime.Status);
        Assert.Equal(AgentRuntimeStatus.Queued, waiting.Status);
        Assert.Empty(runner.Starts);
        Assert.Single(runner.Stops);
        var stopping = Assert.Single(donor.Events, x => x.Status == AgentRuntimeStatus.Stopping);
        stopping.OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        runner.ConfirmStop = true;
        runner.ConfirmDestroy = true;

        await manager.ReconcileAsync();
        await manager.ReconcileAsync();

        Assert.Equal(AgentRuntimeStatus.Cancelled, donor.Status);
        Assert.Contains("waiting agent capacity", donor.Reason);
        Assert.Equal(AgentRuntimeStatus.WaitingForMcpSession, waiting.Status);
        Assert.Equal(AgentRuntimeStatus.Running, otherRuntime.Status);
        Assert.Single(runner.Starts);
        Assert.Single(donor.Events, x => x.Status == AgentRuntimeStatus.Stopping);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task OfficePressure_ReleasesOnlyAnActualReservationInTheWaitingPool(bool sharedPool, bool explicitPool)
    {
        await using var db = CreateDb();
        var (donor, waiting, _) = await SeedCapacityPressureAsync(db);
        var settings = await db.AgentRuntimeGlobalSettings.SingleAsync();
        settings.GlobalMaxActiveWorkloads = 10;
        var pool = Guid.NewGuid();
        settings.DefaultRuntimeExecutionPoolId = pool;
        if (explicitPool) waiting.AgentInstallation!.ExecutionPoolId = pool;
        // Installation metadata is intentionally different: the running reservation is authoritative.
        donor.AgentInstallation!.ExecutionPoolId = Guid.NewGuid();
        db.Add(new ExecutionWorkloadAssignment { Id = Guid.NewGuid(), AgentRuntimeInstanceId = donor.Id,
            ExecutionPoolId = sharedPool ? pool : Guid.NewGuid(), WorkloadKind = ExecutionWorkloadKind.Runtime,
            Status = ExecutionAssignmentStatus.Running });
        await db.SaveChangesAsync();
        var runner = new FakeRunner { BeforeStart = _ => donor.Status == AgentRuntimeStatus.Cancelled
            ? Task.CompletedTask : throw new AgentWorkloadCapacityUnavailableException("Office is full.") };
        var manager = CreateManager(db, runner);

        await manager.ReconcileAsync();
        await manager.ReconcileAsync();

        Assert.Equal(sharedPool ? AgentRuntimeStatus.Cancelled : AgentRuntimeStatus.Running, donor.Status);
        Assert.Equal(sharedPool ? AgentRuntimeStatus.WaitingForMcpSession : AgentRuntimeStatus.Queued, waiting.Status);
        Assert.Equal(sharedPool ? 1 : 0, runner.Starts.Count);
    }

    private static async Task<(AgentRuntimeInstance Donor, AgentRuntimeInstance Waiting, AgentWorkItem Work)>
        SeedCapacityPressureAsync(CSweet.Infrastructure.Persistence.CSweetDbContext db, bool businessLimit = false)
    {
        var installation = await SeedAsync(db, due: false, globalLimit: businessLimit ? 10 : 1);
        installation.Schedule!.ActivationMode = ActivationMode.OnDemand;
        var donor = RunningInstance(installation.Id);
        donor.AgentInstallation = installation;
        donor.ProviderInstanceId = "idle-vm";
        donor.IdleDeadlineAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var next = await SeedAsync(db, due: false);
        next.Schedule!.ActivationMode = ActivationMode.OnDemand;
        var waiting = new AgentRuntimeInstance { Id = Guid.NewGuid(), TickId = Guid.NewGuid(),
            AgentInstallationId = next.Id, AgentInstallation = next, QueuedAt = DateTimeOffset.UtcNow };
        var work = CapacityWork(next.Id);
        db.AddRange(donor, waiting, work, SessionFor(donor, installation, DateTimeOffset.UtcNow.AddMinutes(10)));
        if (businessLimit) (await db.AgentRuntimeGlobalSettings.SingleAsync()).PerBusinessMaxActiveWorkloads = 1;
        await db.SaveChangesAsync();
        return (donor, waiting, work);
    }

    private static AgentWorkItem CapacityWork(Guid installationId) => new()
    {
        Id = Guid.NewGuid(), AgentInstallationId = installationId, OrganizationId = "business-1",
        Kind = AgentWorkKind.Capability, Name = "work.execution.run", Status = AgentWorkStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow, AvailableAt = DateTimeOffset.UtcNow.AddSeconds(-1),
        DeadlineAt = DateTimeOffset.UtcNow.AddMinutes(10), IdempotencyKey = Guid.NewGuid().ToString("N")
    };
}
