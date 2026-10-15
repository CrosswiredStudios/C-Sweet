using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentRuntimeManagerTests
{
    [Fact]
    public async Task NoninteractiveOnDemandRuntime_HasIdleDeadline()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        installation.Schedule!.ActivationMode = ActivationMode.OnDemand;
        await db.SaveChangesAsync();

        await CreateManager(db, new FakeRunner()).EnsureRuntimeQueuedAsync(installation.Id, "Sprint ticket ready.");

        var runtime = await db.AgentRuntimeInstances.SingleAsync();
        Assert.False(runtime.IsInteractive);
        Assert.True(runtime.IdleDeadlineAt > DateTimeOffset.UtcNow);
    }

    [Theory]
    [InlineData(ActivationMode.OnDemand, true)]
    [InlineData(ActivationMode.AlwaysOn, false)]
    [InlineData(ActivationMode.Scheduled, false)]
    public async Task MissingIdleDeadline_ReleasesOnlyIdleOnDemandRuntime(ActivationMode mode, bool shouldStop)
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        installation.Schedule!.ActivationMode = mode;
        var runtime = RunningInstance(installation.Id, DateTimeOffset.UtcNow.AddMinutes(-10));
        runtime.ProviderInstanceId = "idle-vm";
        db.AgentRuntimeInstances.Add(runtime);
        db.McpAgentSessions.Add(SessionFor(runtime, installation, DateTimeOffset.UtcNow.AddMinutes(10)));
        await db.SaveChangesAsync();
        var runner = new FakeRunner();

        var manager = CreateManager(db, runner);
        await manager.ReconcileAsync();

        Assert.Equal(shouldStop ? AgentRuntimeStatus.Cancelled : AgentRuntimeStatus.Running, runtime.Status);
        Assert.Equal(shouldStop ? 1 : 0, runner.Stops.Count);
        if (shouldStop)
        {
            Assert.Contains("idle timeout", runtime.Reason);
            Assert.NotNull((await db.McpAgentSessions.SingleAsync()).RevokedAt);
            Assert.False(await manager.ReleaseIdleRuntimeAsync(runtime.Id));
            Assert.Single(runner.Stops);
        }
    }

    [Theory]
    [InlineData(AgentWorkStatus.Pending, false, false)]
    [InlineData(AgentWorkStatus.Leased, false, false)]
    [InlineData(AgentWorkStatus.Completed, false, false)]
    [InlineData(AgentWorkStatus.Completed, true, true)]
    public async Task IdleRelease_PreservesWorkButPeriodicReviewsDoNotExtendLifetime(
        AgentWorkStatus status, bool attentionReview, bool shouldStop)
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        installation.Schedule!.ActivationMode = ActivationMode.OnDemand;
        var runtime = RunningInstance(installation.Id, DateTimeOffset.UtcNow.AddMinutes(-10));
        runtime.ProviderInstanceId = "idle-vm";
        var now = DateTimeOffset.UtcNow;
        var work = new AgentWorkItem
        {
            Id = Guid.NewGuid(), AgentInstallationId = installation.Id, OrganizationId = installation.BusinessId,
            Kind = attentionReview ? AgentWorkKind.Event : AgentWorkKind.Capability,
            Name = attentionReview ? CSweet.Agent.SDK.AgentAttentionEvents.ReviewDue : "work.execution.run",
            Status = status, CreatedAt = now.AddMinutes(-8), AvailableAt = now.AddMinutes(-8),
            DeadlineAt = now.AddMinutes(10), CompletedAt = status == AgentWorkStatus.Completed ? now.AddSeconds(-10) : null
        };
        if (status != AgentWorkStatus.Pending)
            work.Attempts.Add(new AgentWorkAttempt
            {
                Id = Guid.NewGuid(), AgentWorkItemId = work.Id, RuntimeInstanceId = runtime.Id, Attempt = 1,
                ClaimedAt = now.AddMinutes(-8), LeaseExpiresAt = now.AddMinutes(2),
                FinishedAt = work.CompletedAt
            });
        db.AgentWorkItems.Add(work);
        db.AgentRuntimeInstances.Add(runtime);
        db.McpAgentSessions.Add(SessionFor(runtime, installation, now.AddMinutes(10)));
        await db.SaveChangesAsync();
        var runner = new FakeRunner();

        await CreateManager(db, runner).ReconcileAsync();

        Assert.Equal(shouldStop ? AgentRuntimeStatus.Cancelled : AgentRuntimeStatus.Running, runtime.Status);
        Assert.Equal(status, work.Status);
        Assert.Equal(shouldStop ? 1 : 0, runner.Stops.Count);
        if (status == AgentWorkStatus.Completed && !attentionReview)
            Assert.True(runtime.IdleDeadlineAt > now);
    }

    [Fact]
    public async Task IdleRelease_WaitsForConfirmedRemoteStopAndDoesNotRepeatStopTransition()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        installation.Schedule!.ActivationMode = ActivationMode.OnDemand;
        var runtime = RunningInstance(installation.Id, DateTimeOffset.UtcNow.AddMinutes(-10));
        runtime.ProviderInstanceId = "idle-vm";
        db.AgentRuntimeInstances.Add(runtime);
        await db.SaveChangesAsync();
        var runner = new FakeRunner { ConfirmStop = false };
        var manager = CreateManager(db, runner);

        await manager.ReconcileAsync();
        await manager.ReconcileAsync();

        Assert.Equal(AgentRuntimeStatus.Stopping, runtime.Status);
        Assert.Single(runtime.Events, x => x.Status == AgentRuntimeStatus.Stopping);
        Assert.Null(runtime.CompletedAt);
        Assert.Empty(runner.Removes);

        var stopping = Assert.Single(runtime.Events, x => x.Status == AgentRuntimeStatus.Stopping);
        stopping.OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        runner.ConfirmDestroy = true;
        await manager.ReconcileAsync();

        Assert.Equal(AgentRuntimeStatus.Cancelled, runtime.Status);
        Assert.Contains("idle timeout", runtime.Reason);
        Assert.NotNull(runtime.CompletedAt);
    }
}
