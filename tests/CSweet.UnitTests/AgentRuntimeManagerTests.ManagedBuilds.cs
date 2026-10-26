using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentRuntimeManagerTests
{
    [Fact]
    public async Task ManagedBuildRuntimeIsNotLaunchedByOrdinaryAgentReconciliation()
    {
        await using var db = CreateDb(); var installation = await SeedAsync(db, due: false);
        var runtime = new AgentRuntimeInstance { Id = Guid.NewGuid(), TickId = Guid.NewGuid(),
            AgentInstallationId = installation.Id, DeliveryBuildId = Guid.NewGuid(), QueuedAt = DateTimeOffset.UtcNow };
        db.AgentRuntimeInstances.Add(runtime); await db.SaveChangesAsync();
        var runner = new FakeRunner(); await CreateManager(db, runner).ReconcileAsync();
        Assert.Empty(runner.Starts); Assert.Equal(AgentRuntimeStatus.Queued, runtime.Status);
    }

    [Fact]
    public async Task OrdinaryInteractiveRuntimeDoesNotReuseManagedBuildIdentity()
    {
        await using var db = CreateDb(); var installation = await SeedAsync(db, due: false);
        installation.Schedule!.ActivationMode = ActivationMode.OnDemand; installation.Schedule.NextTickAt = null;
        var runtime = new AgentRuntimeInstance { Id = Guid.NewGuid(), TickId = Guid.NewGuid(),
            AgentInstallationId = installation.Id, DeliveryBuildId = Guid.NewGuid(), QueuedAt = DateTimeOffset.UtcNow };
        db.AgentRuntimeInstances.Add(runtime); await db.SaveChangesAsync();
        var runner = new FakeRunner(); await new CSweet.Infrastructure.Setup.AgentInteractiveRuntimeService(db, CreateManager(db, runner)).EnsureReadyAsync(installation.Id);
        Assert.Single(runner.Starts); Assert.Equal(2, await db.AgentRuntimeInstances.CountAsync());
        Assert.Equal(AgentRuntimeStatus.Queued, runtime.Status);
    }
}
