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
using AgentWorkStatus = CSweet.Domain.Setup.AgentWorkStatus;

namespace CSweet.UnitTests;

public sealed partial class AgentAttentionSchedulerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SleepingOnDemandInstallationGetsOneCurrentStartupOrDueReview(bool startup)
    {
        await using var fixture = new AttentionFixture();
        var installation = await fixture.SeedAsync(startup: startup);

        await fixture.DispatchAsync();
        await fixture.DispatchAsync();

        var work = Assert.Single(await fixture.Db.AgentWorkItems.ToListAsync());
        Assert.Equal(AgentWorkStatus.Pending, work.Status);
        Assert.Equal(AgentAttentionEvents.ReviewDue, work.Name);
        Assert.Equal(installation.Id, work.AgentInstallationId);
        Assert.Equal(fixture.Clock.Now, work.AvailableAt);
        Assert.Equal(fixture.Clock.Now, installation.Schedule!.LastAttentionReviewAt);
        Assert.Equal(fixture.Clock.Now.AddMinutes(5), installation.Schedule.NextAttentionReviewAt);
        // No VM/session is needed to create demand; normal pending-work discovery starts it.
        Assert.Empty(await fixture.Db.AgentRuntimeInstances.ToListAsync());
    }

    [Theory]
    [InlineData(ActivationMode.AlwaysOn)]
    [InlineData(ActivationMode.Scheduled)]
    public async Task OfflineNonOnDemandInstallationDoesNotGetAnUnscheduledWake(ActivationMode mode)
    {
        await using var fixture = new AttentionFixture();
        await fixture.SeedAsync(mode: mode);
        await fixture.DispatchAsync();
        Assert.Empty(await fixture.Db.AgentWorkItems.ToListAsync());
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("schedule-disabled")]
    [InlineData("unsubscribed")]
    [InlineData("setup")]
    public async Task SleepingInstallationStillRequiresEnabledReadySubscription(string condition)
    {
        await using var fixture = new AttentionFixture();
        var installation = await fixture.SeedAsync();
        if (condition == "disabled") installation.IsEnabled = false;
        if (condition == "schedule-disabled") installation.Schedule!.IsEnabled = false;
        if (condition == "unsubscribed") installation.Grant!.EventSubscriptionsJson = "[]";
        if (condition == "setup") installation.SetupState = PluginSetupState.NeedsSetup;
        await fixture.Db.SaveChangesAsync();

        await fixture.DispatchAsync();

        Assert.Empty(await fixture.Db.AgentWorkItems.ToListAsync());
        Assert.Null(installation.Schedule!.LastAttentionReviewAt);
    }

    [Fact]
    public async Task SleepingInstallationWaitsUntilItsNextReviewIsDue()
    {
        await using var fixture = new AttentionFixture();
        var installation = await fixture.SeedAsync(startup: false);
        installation.Schedule!.NextAttentionReviewAt = fixture.Clock.Now.AddHours(1);
        await fixture.Db.SaveChangesAsync();
        await fixture.DispatchAsync();
        Assert.Empty(await fixture.Db.AgentWorkItems.ToListAsync());
    }

    [Fact]
    public async Task StateChangeWakesSleepingInstallationBeforeItsPeriodicReview()
    {
        await using var fixture = new AttentionFixture();
        var installation = await fixture.SeedAsync(startup: false);
        installation.Schedule!.NextAttentionReviewAt = fixture.Clock.Now.AddHours(1);
        await fixture.Db.SaveChangesAsync();
        var correlation = Guid.NewGuid();
        await new AgentAttentionInvalidationService(fixture.Db, fixture.Clock)
            .InvalidateAsync([installation.Id], "workforce.role-changed", correlation);

        await fixture.DispatchAsync();

        var work = await fixture.Db.AgentWorkItems.SingleAsync();
        Assert.Contains(correlation.ToString("N"), work.IdempotencyKey);
        Assert.Null(installation.Schedule.PendingAttentionReason);
    }

    [Fact]
    public async Task LaterInvalidationWithTheSameDomainCorrelationCanWakeAgain()
    {
        await using var fixture = new AttentionFixture();
        var installation = await fixture.SeedAsync(startup: false);
        var correlation = Guid.NewGuid();
        var invalidator = new AgentAttentionInvalidationService(fixture.Db, fixture.Clock);
        await invalidator.InvalidateAsync([installation.Id], "workforce.role-changed", correlation);
        await fixture.DispatchAsync();
        var first = await fixture.Db.AgentWorkItems.SingleAsync();
        first.Status = AgentWorkStatus.Completed;
        await fixture.Db.SaveChangesAsync();
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(10);

        await invalidator.InvalidateAsync([installation.Id], "workforce.role-changed", correlation);
        await fixture.DispatchAsync();
        await fixture.DispatchAsync();

        Assert.Equal(2, await fixture.Db.AgentWorkItems.CountAsync());
        Assert.Null(installation.Schedule!.PendingAttentionReason);
    }

    [Theory]
    [InlineData(AgentWorkStatus.Pending)]
    [InlineData(AgentWorkStatus.Leased)]
    public async Task OfflineBacklogCoalescesReviewsAndRetainsNewStateChanges(AgentWorkStatus status)
    {
        await using var fixture = new AttentionFixture();
        var installation = await fixture.SeedAsync(startup: false);
        await fixture.DispatchAsync();
        var first = await fixture.Db.AgentWorkItems.SingleAsync();
        first.Status = status;
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(6);
        await fixture.Db.SaveChangesAsync();
        var invalidator = new AgentAttentionInvalidationService(fixture.Db, fixture.Clock);
        var correlation = Guid.NewGuid();
        await invalidator.InvalidateAsync([installation.Id], "workforce.role-changed", correlation);

        await fixture.DispatchAsync();
        await fixture.DispatchAsync();
        Assert.Single(await fixture.Db.AgentWorkItems.ToListAsync());
        Assert.Equal(correlation, installation.Schedule!.PendingAttentionCorrelationId);

        first.Status = AgentWorkStatus.Completed;
        first.CompletedAt = fixture.Clock.Now;
        await fixture.Db.SaveChangesAsync();
        await fixture.DispatchAsync();
        await fixture.DispatchAsync();

        var second = Assert.Single(await fixture.Db.AgentWorkItems.Where(x => x.Id != first.Id).ToListAsync());
        Assert.Contains(correlation.ToString("N"), second.IdempotencyKey);
        Assert.Null(installation.Schedule.PendingAttentionReason);
    }

    [Theory]
    [InlineData(AgentWorkStatus.Pending)]
    [InlineData(AgentWorkStatus.Leased)]
    public async Task ExpiredReviewWithoutALiveLeaseDoesNotPreventOfflineRecovery(AgentWorkStatus status)
    {
        await using var fixture = new AttentionFixture();
        await fixture.SeedAsync(startup: false);
        await fixture.DispatchAsync();
        var first = await fixture.Db.AgentWorkItems.SingleAsync();
        first.Status = status;
        await fixture.Db.SaveChangesAsync();
        fixture.Clock.Now = first.DeadlineAt.AddMinutes(30);

        await fixture.DispatchAsync();
        await fixture.DispatchAsync();

        Assert.Equal(2, await fixture.Db.AgentWorkItems.CountAsync());
        Assert.Single(await fixture.Db.AgentWorkItems.Where(x => x.DeadlineAt > fixture.Clock.Now).ToListAsync());
    }

    [Fact]
    public async Task LiveLeaseProtectsAnExpiredReviewFromDuplicateDispatch()
    {
        await using var fixture = new AttentionFixture();
        await fixture.SeedAsync(startup: false);
        await fixture.DispatchAsync();
        var work = await fixture.Db.AgentWorkItems.SingleAsync();
        work.Status = AgentWorkStatus.Leased;
        fixture.Clock.Now = work.DeadlineAt.AddMinutes(1);
        fixture.Db.AgentWorkAttempts.Add(new AgentWorkAttempt
        {
            Id = Guid.NewGuid(), AgentWorkItemId = work.Id, RuntimeInstanceId = Guid.NewGuid(), Attempt = 1,
            ClaimedAt = fixture.Clock.Now.AddMinutes(-5), LeaseExpiresAt = fixture.Clock.Now.AddMinutes(2)
        });
        await fixture.Db.SaveChangesAsync();

        await fixture.DispatchAsync();

        Assert.Single(await fixture.Db.AgentWorkItems.ToListAsync());
    }

    private sealed class AttentionFixture : IAsyncDisposable
    {
        public FixedTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        public CSweetDbContext Db { get; } = new(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        private readonly ServiceProvider _services;
        public AttentionFixture()
        {
            var services = new ServiceCollection();
            services.AddSingleton(Db);
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddScoped<AgentWorkInbox>();
            services.AddScoped<AgentWorkRouter>();
            _services = services.BuildServiceProvider();
        }

        public Task DispatchAsync() => new AgentAttentionScheduler(_services.GetRequiredService<IServiceScopeFactory>(),
            Clock, NullLogger<AgentAttentionScheduler>.Instance).DispatchDueReviewsAsync(CancellationToken.None);

        public async Task<AgentInstallation> SeedAsync(bool startup = true, ActivationMode mode = ActivationMode.OnDemand)
        {
            var id = Guid.NewGuid();
            var installation = new AgentInstallation
            {
                Id = id, InstallationKey = Guid.NewGuid(), PackageVersionId = Guid.NewGuid(),
                BusinessId = Guid.NewGuid().ToString("D"), IsEnabled = true,
                RevisionStatus = PluginRevisionStatus.Active, SetupState = PluginSetupState.Ready,
                CreatedAt = Clock.Now.AddDays(-1), UpdatedAt = Clock.Now,
                Grant = new AgentInstallationGrant
                {
                    Id = Guid.NewGuid(), AgentInstallationId = id,
                    EventSubscriptionsJson = JsonSerializer.Serialize(new[] { AgentAttentionEvents.ReviewDue })
                },
                Schedule = new AgentSchedule
                {
                    Id = Guid.NewGuid(), AgentInstallationId = id, ActivationMode = mode,
                    TickFrequencySeconds = 300, IsEnabled = true, MaxRuntimeSeconds = 300,
                    LastAttentionReviewAt = startup ? null : Clock.Now.AddHours(-7),
                    NextAttentionReviewAt = Clock.Now.AddHours(-6)
                }
            };
            Db.Add(installation);
            await Db.SaveChangesAsync();
            return installation;
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await Db.DisposeAsync();
        }
    }
}
