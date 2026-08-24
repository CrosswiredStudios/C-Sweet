using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed class AgentAttentionSchedulerTests
{
    [Fact]
    public async Task StateChangesCoalesceAndDispatchOneTargetedImmediateReview()
    {
        var now = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(now);
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var organizationId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var installation = new AgentInstallation
        {
            Id = installationId, InstallationKey = Guid.NewGuid(), PackageVersionId = Guid.NewGuid(),
            BusinessId = organizationId.ToString("D"), IsEnabled = true,
            RevisionStatus = PluginRevisionStatus.Active, SetupState = PluginSetupState.Ready,
            CreatedAt = now.AddDays(-1), UpdatedAt = now,
            Grant = new AgentInstallationGrant
            {
                Id = Guid.NewGuid(), AgentInstallationId = installationId,
                EventSubscriptionsJson = JsonSerializer.Serialize(new[] { AgentAttentionEvents.ReviewDue }),
                RequiredCapabilitiesJson = "[]", ProvidedCapabilitiesJson = "[]", NetworkAccessJson = "[]"
            },
            Schedule = new AgentSchedule
            {
                Id = Guid.NewGuid(), AgentInstallationId = installationId,
                ActivationMode = ActivationMode.AlwaysOn, TickFrequencySeconds = 300,
                IsEnabled = true, NextAttentionReviewAt = now.AddHours(1),
                LastAttentionReviewAt = now.AddMinutes(-1), MaxRuntimeSeconds = 300,
                OverlapPolicy = OverlapPolicy.Skip
            }
        };
        var runtime = new AgentRuntimeInstance
        {
            Id = Guid.NewGuid(), TickId = Guid.NewGuid(), AgentInstallationId = installationId,
            QueuedAt = now.AddMinutes(-2)
        };
        runtime.TransitionTo(AgentRuntimeStatus.Starting, now.AddMinutes(-2));
        runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, now.AddMinutes(-2));
        runtime.TransitionTo(AgentRuntimeStatus.Running, now.AddMinutes(-2));
        db.AddRange(installation, runtime);
        await db.SaveChangesAsync();

        var firstCorrelation = Guid.NewGuid();
        var latestCorrelation = Guid.NewGuid();
        var invalidator = new AgentAttentionInvalidationService(db, clock);
        await invalidator.InvalidateAsync([installationId], "workforce.role-changed", firstCorrelation);
        await invalidator.InvalidateAsync([installationId], "workforce.team-membership-changed", latestCorrelation);
        Assert.Equal(now, installation.Schedule!.NextAttentionReviewAt);
        Assert.Equal(now, installation.Schedule.AttentionInvalidatedAt);

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddScoped<AgentWorkInbox>();
        services.AddScoped<AgentWorkRouter>();
        await using var provider = services.BuildServiceProvider();
        var scheduler = new AgentAttentionScheduler(provider.GetRequiredService<IServiceScopeFactory>(),
            clock, NullLogger<AgentAttentionScheduler>.Instance);

        await scheduler.DispatchDueReviewsAsync(CancellationToken.None);
        await scheduler.DispatchDueReviewsAsync(CancellationToken.None);

        var work = Assert.Single(await db.AgentWorkItems.Where(x => x.Name == AgentAttentionEvents.ReviewDue).ToListAsync());
        Assert.Contains(latestCorrelation.ToString("N"), work.IdempotencyKey, StringComparison.Ordinal);
        Assert.Null(installation.Schedule.PendingAttentionReason);
    }

    [Fact]
    public async Task ReconnectionQueuesOneCurrentReviewAndDoesNotAccumulateMissedIntervals()
    {
        var now = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(now);
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var organizationId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var installation = new AgentInstallation
        {
            Id = installationId, InstallationKey = Guid.NewGuid(), PackageVersionId = Guid.NewGuid(),
            BusinessId = organizationId.ToString("D"), IsEnabled = true,
            RevisionStatus = PluginRevisionStatus.Active, SetupState = PluginSetupState.Ready,
            CreatedAt = now.AddDays(-1), UpdatedAt = now,
            Grant = new AgentInstallationGrant
            {
                Id = Guid.NewGuid(), AgentInstallationId = installationId,
                EventSubscriptionsJson = JsonSerializer.Serialize(new[] { AgentAttentionEvents.ReviewDue }),
                RequiredCapabilitiesJson = "[]", ProvidedCapabilitiesJson = "[]", NetworkAccessJson = "[]"
            },
            Schedule = new AgentSchedule
            {
                Id = Guid.NewGuid(), AgentInstallationId = installationId,
                ActivationMode = ActivationMode.AlwaysOn, TickFrequencySeconds = 300,
                IsEnabled = true, NextAttentionReviewAt = now.AddHours(-6),
                LastAttentionReviewAt = now.AddHours(-7), MaxRuntimeSeconds = 300,
                OverlapPolicy = OverlapPolicy.Skip
            }
        };
        var runtime = new AgentRuntimeInstance
        {
            Id = Guid.NewGuid(), TickId = Guid.NewGuid(), AgentInstallationId = installationId,
            QueuedAt = now.AddMinutes(-1)
        };
        runtime.TransitionTo(AgentRuntimeStatus.Starting, now.AddMinutes(-1));
        runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, now.AddSeconds(-20));
        runtime.TransitionTo(AgentRuntimeStatus.Running, now.AddSeconds(-10));
        db.AddRange(installation, runtime);
        await db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddScoped<AgentWorkInbox>();
        services.AddScoped<AgentWorkRouter>();
        await using var provider = services.BuildServiceProvider();
        var scheduler = new AgentAttentionScheduler(
            provider.GetRequiredService<IServiceScopeFactory>(), clock,
            NullLogger<AgentAttentionScheduler>.Instance);

        await scheduler.DispatchDueReviewsAsync(CancellationToken.None);
        await scheduler.DispatchDueReviewsAsync(CancellationToken.None);

        var work = Assert.Single(await db.AgentWorkItems.Where(x =>
            x.Name == AgentAttentionEvents.ReviewDue).ToListAsync());
        Assert.Equal(AgentWorkStatus.Pending, work.Status);
        var schedule = await db.AgentSchedules.SingleAsync();
        Assert.Equal(now.AddMinutes(5), schedule.NextAttentionReviewAt);
        Assert.Equal(now, schedule.LastAttentionReviewAt);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
