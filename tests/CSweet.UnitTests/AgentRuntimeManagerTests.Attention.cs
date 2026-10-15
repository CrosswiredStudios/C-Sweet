using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using AgentWorkStatus = CSweet.Domain.Setup.AgentWorkStatus;

namespace CSweet.UnitTests;

public sealed partial class AgentRuntimeManagerTests
{
    [Fact]
    public async Task SleepingAttentionReviewIsRediscoveredAndQueuesOneOnDemandRuntime()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        installation.Schedule!.ActivationMode = ActivationMode.OnDemand;
        installation.Schedule.NextTickAt = null;
        installation.Schedule.LastAttentionReviewAt = DateTimeOffset.UtcNow.AddHours(-2);
        installation.Schedule.NextAttentionReviewAt = DateTimeOffset.UtcNow.AddHours(-1);
        installation.SetupState = PluginSetupState.Ready;
        installation.RevisionStatus = PluginRevisionStatus.Active;
        installation.Grant!.EventSubscriptionsJson = JsonSerializer.Serialize(new[] { AgentAttentionEvents.ReviewDue });
        await db.SaveChangesAsync();
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddScoped<AgentWorkInbox>();
        services.AddScoped<AgentWorkRouter>();
        await using var provider = services.BuildServiceProvider();
        var scheduler = new AgentAttentionScheduler(provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System, NullLogger<AgentAttentionScheduler>.Instance);

        await scheduler.DispatchDueReviewsAsync(CancellationToken.None);
        await scheduler.DispatchDueReviewsAsync(CancellationToken.None);
        var work = await db.AgentWorkItems.SingleAsync();
        Assert.Equal(AgentAttentionEvents.ReviewDue, work.Name);
        Assert.Empty(await db.AgentRuntimeInstances.ToListAsync());

        // Simulate the independent runtime reconciliation after missed immediate activation.
        var manager = CreateManager(db, new FakeRunner());
        Assert.Equal(1, await manager.EnsurePendingOnDemandRuntimesAsync());
        Assert.Equal(0, await manager.EnsurePendingOnDemandRuntimesAsync());
        var runtime = await db.AgentRuntimeInstances.SingleAsync();
        Assert.Equal(installation.Id, runtime.AgentInstallationId);
        Assert.Equal(AgentRuntimeStatus.Queued, runtime.Status);
        Assert.NotNull(runtime.IdleDeadlineAt);
        Assert.Equal(AgentWorkStatus.Pending, work.Status);
    }
}
