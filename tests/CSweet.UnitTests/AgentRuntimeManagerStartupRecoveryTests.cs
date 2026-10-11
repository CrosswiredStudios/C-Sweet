using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentRuntimeManagerTests
{
    [Fact]
    public async Task TransientStartupFailures_ContinueAfterThreeWithBackoffAndRespectDueTime()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        installation.Schedule!.ActivationMode = ActivationMode.AlwaysOn;
        installation.Schedule.OverlapPolicy = OverlapPolicy.CancelPrevious;
        installation.Schedule.NextTickAt = null;
        var manager = CreateManager(db, new FakeRunner { Logs = "MCP initialization: 502 (Bad Gateway)" });
        for (var failure = 1; failure <= 8; failure++)
        {
            var runtime = new AgentRuntimeInstance
            {
                Id = Guid.NewGuid(), TickId = Guid.NewGuid(), AgentInstallationId = installation.Id,
                QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-2), BrokerTokenHash = new string('0', 64),
                RuntimeDeadlineAt = DateTimeOffset.UtcNow.AddMinutes(5),
                IsolationProviderId = "test-vm", ProviderInstanceId = "unresponsive-vm"
            };
            runtime.TransitionTo(AgentRuntimeStatus.Starting, DateTimeOffset.UtcNow.AddMinutes(-2));
            runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, DateTimeOffset.UtcNow.AddMinutes(-1));
            db.AgentRuntimeInstances.Add(runtime);
            await db.SaveChangesAsync();
            var before = DateTimeOffset.UtcNow;
            Assert.Equal(1, await manager.ReconcileAsync());
            Assert.Equal(failure, installation.Schedule.ConsecutiveStartupFailures);
            Assert.Null(installation.Schedule.AutomaticStartSuppressedAt);
            var delay = Math.Min(1800, 30 * Math.Pow(2, failure - 1));
            Assert.True(installation.Schedule.NextTickAt >= before.AddSeconds(delay));
            Assert.True(installation.Schedule.NextTickAt <= DateTimeOffset.UtcNow.AddSeconds(delay));
            Assert.Equal(0, await manager.EnsureAlwaysOnRuntimesAsync());
            Assert.Equal(0, await manager.ProcessDueSchedulesAsync());
        }
        installation.Schedule.NextTickAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        Assert.Equal(1, await manager.EnsureAlwaysOnRuntimesAsync());
        Assert.Equal(0, await manager.EnsureAlwaysOnRuntimesAsync());
        Assert.Equal(0, await manager.ProcessDueSchedulesAsync());
        Assert.Single(await db.AgentRuntimeInstances.Where(x => x.Status == AgentRuntimeStatus.Queued).ToListAsync());
    }

    [Theory]
    [InlineData(true, true, 1, false)]
    [InlineData(true, true, 1, true)]
    [InlineData(true, false, 0, false)]
    [InlineData(false, true, 0, false)]
    public async Task ServerRestart_RecoversOnlyEnabledTransientFailures(bool transient, bool enabled, int expected, bool deferred)
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        installation.IsEnabled = enabled;
        installation.Schedule!.ActivationMode = ActivationMode.AlwaysOn;
        installation.Schedule.ConsecutiveStartupFailures = 3;
        installation.Schedule.AutomaticStartSuppressedAt = deferred ? null : DateTimeOffset.UtcNow.AddHours(-1);
        installation.Schedule.NextTickAt = deferred ? DateTimeOffset.UtcNow.AddMinutes(30) : null;
        var runtime = new AgentRuntimeInstance
        {
            Id = Guid.NewGuid(), TickId = Guid.NewGuid(), AgentInstallationId = installation.Id,
            QueuedAt = DateTimeOffset.UtcNow.AddHours(-1)
        };
        runtime.TransitionTo(AgentRuntimeStatus.Failed, DateTimeOffset.UtcNow.AddHours(-1));
        // Older stop cleanup overwrote the terminal reason; the original timeout event survives.
        runtime.Events.Add(new AgentRuntimeEvent
        {
            Id = Guid.NewGuid(), AgentRuntimeInstanceId = runtime.Id, Status = AgentRuntimeStatus.Stopping,
            Reason = transient ? "MCP session establishment timed out." : "Manifest version mismatch.",
            OccurredAt = runtime.QueuedAt
        });
        db.AgentRuntimeInstances.Add(runtime);
        await db.SaveChangesAsync();
        var service = new AgentRuntimeStartupCleanupService(db, new FakeRunner(),
            Options.Create(new AgentRuntimeManagerOptions()), NullLogger<AgentRuntimeStartupCleanupService>.Instance);
        Assert.Equal(expected, await service.RecoverTransientStartupFailuresAsync());
        Assert.Equal(0, await service.RecoverTransientStartupFailuresAsync());
        Assert.Equal(3, installation.Schedule.ConsecutiveStartupFailures);
        Assert.Equal(AgentRuntimeStatus.Failed, runtime.Status);
        if (expected == 1)
        {
            Assert.Null(installation.Schedule.AutomaticStartSuppressedAt);
            Assert.True(installation.Schedule.NextTickAt <= DateTimeOffset.UtcNow);
            Assert.Equal(1, await CreateManager(db, new FakeRunner()).EnsureAlwaysOnRuntimesAsync());
        }
    }
}
