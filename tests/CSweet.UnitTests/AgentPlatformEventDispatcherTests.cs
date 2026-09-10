using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Application.Setup;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed class AgentPlatformEventDispatcherTests
{
    [Fact]
    public async Task DispatchPendingAsync_QueuesEverySubscribedRecipientRuntime()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var runtime = new RecordingRuntimeManager();
        var services = new ServiceCollection();
        services.AddDbContext<CSweetDbContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<AgentWorkInbox>();
        services.AddScoped<AgentWorkRouter>();
        services.AddSingleton<IAgentRuntimeManager>(runtime);
        await using var provider = services.BuildServiceProvider();
        var organizationId = Guid.NewGuid();
        var subscribed = Installation(organizationId, [HiringEvents.RecommendationFulfilled]);
        var alsoSubscribed = Installation(organizationId, [HiringEvents.RecommendationFulfilled]);
        var unrelated = Installation(organizationId, [HiringEvents.EmployeeHired]);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            db.AddRange(subscribed, alsoSubscribed, unrelated);
            var now = DateTimeOffset.UtcNow;
            db.AgentPlatformEventOutbox.Add(new AgentPlatformEventOutboxItem
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                EventType = HiringEvents.RecommendationFulfilled,
                DataJson = "{}",
                IdempotencyKey = "recommendation-fulfilled",
                Status = AgentPlatformEventOutboxStatus.Pending,
                NextAttemptAt = now,
                OccurredAt = now
            });
            await db.SaveChangesAsync();
        }

        var dispatcher = new AgentPlatformEventDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<AgentPlatformEventDispatcher>.Instance);
        await dispatcher.DispatchPendingAsync(CancellationToken.None);

        Assert.Equal(
            new[] { subscribed.Id, alsoSubscribed.Id }.Order().ToArray(),
            runtime.QueuedInstallationIds.Order().ToArray());
        await using var verificationScope = provider.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        Assert.Equal(
            AgentPlatformEventOutboxStatus.Published,
            (await verificationDb.AgentPlatformEventOutbox.SingleAsync()).Status);
        Assert.Equal(2, await verificationDb.AgentWorkItems.CountAsync());
    }

    [Fact]
    public async Task DispatchPendingAsync_PublishesAfterDurableDeliveryWhenRuntimeActivationIsDelayed()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var runtime = new RecordingRuntimeManager { FailNext = true };
        var services = new ServiceCollection();
        services.AddDbContext<CSweetDbContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<AgentWorkInbox>();
        services.AddScoped<AgentWorkRouter>();
        services.AddSingleton<IAgentRuntimeManager>(runtime);
        await using var provider = services.BuildServiceProvider();
        var organizationId = Guid.NewGuid();
        var subscribed = Installation(organizationId, [HiringEvents.RecommendationFulfilled]);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            db.Add(subscribed);
            var now = DateTimeOffset.UtcNow;
            db.AgentPlatformEventOutbox.Add(new AgentPlatformEventOutboxItem
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                EventType = HiringEvents.RecommendationFulfilled,
                DataJson = "{}",
                IdempotencyKey = "recommendation-fulfilled-retry",
                Status = AgentPlatformEventOutboxStatus.Pending,
                NextAttemptAt = now,
                OccurredAt = now
            });
            await db.SaveChangesAsync();
        }
        var dispatcher = new AgentPlatformEventDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<AgentPlatformEventDispatcher>.Instance);

        await dispatcher.DispatchPendingAsync(CancellationToken.None);
        await using (var verificationScope = provider.CreateAsyncScope())
        {
            var db = verificationScope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            var outbox = await db.AgentPlatformEventOutbox.SingleAsync();
            Assert.Equal(AgentPlatformEventOutboxStatus.Published, outbox.Status);
            Assert.Equal(1, outbox.Attempts);
            Assert.Equal(1, await db.AgentWorkItems.CountAsync());
        }
        Assert.Empty(runtime.QueuedInstallationIds);
    }

    [Fact]
    public async Task PersonalTodoWakeRemainsPendingUntilTargetSubscriptionIsGranted()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var runtime = new RecordingRuntimeManager();
        var services = new ServiceCollection();
        services.AddDbContext<CSweetDbContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<AgentWorkInbox>();
        services.AddScoped<AgentWorkRouter>();
        services.AddSingleton<IAgentRuntimeManager>(runtime);
        await using var provider = services.BuildServiceProvider();
        var organizationId = Guid.NewGuid();
        var installation = Installation(organizationId, []);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            db.Add(installation);
            var now = DateTimeOffset.UtcNow;
            db.AgentPlatformEventOutbox.Add(new AgentPlatformEventOutboxItem
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId,
                TargetInstallationId = installation.Id,
                EventType = CSweet.WorkManagement.Contracts.PersonalTodoEvents.Available,
                DataJson = "{}", IdempotencyKey = "personal-todo-wake",
                Status = AgentPlatformEventOutboxStatus.Pending,
                NextAttemptAt = now, OccurredAt = now
            });
            await db.SaveChangesAsync();
        }

        var dispatcher = new AgentPlatformEventDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            NullLogger<AgentPlatformEventDispatcher>.Instance);
        await dispatcher.DispatchPendingAsync(CancellationToken.None);

        await using var verification = provider.CreateAsyncScope();
        var outbox = await verification.ServiceProvider.GetRequiredService<CSweetDbContext>()
            .AgentPlatformEventOutbox.SingleAsync();
        Assert.Equal(AgentPlatformEventOutboxStatus.Pending, outbox.Status);
        Assert.Equal(1, outbox.Attempts);
        Assert.True(outbox.NextAttemptAt > outbox.OccurredAt);
        Assert.Empty(runtime.QueuedInstallationIds);
    }

    [Fact]
    public async Task CalendarWorkAndReminderReachOnlyTargetInboxAndActivateRuntime()
    {
        var runtime = new RecordingRuntimeManager();
        var services = new ServiceCollection();
        var database = Guid.NewGuid().ToString();
        services.AddDbContext<CSweetDbContext>(o => o.UseInMemoryDatabase(database));
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<AgentWorkInbox>();
        services.AddScoped<AgentWorkRouter>();
        services.AddSingleton<IAgentRuntimeManager>(runtime);
        await using var provider = services.BuildServiceProvider();
        var org = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var agent = Guid.NewGuid();
        var events = new[] { CSweet.WorkManagement.Contracts.CalendarEvents.ReminderDue,
            CSweet.WorkManagement.Contracts.PersonalTodoEvents.Available };
        var target = Installation(org, events);
        var other = Installation(org, events);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            db.AddRange(target, other, new Organization { Id = org, Name = "Calendar" },
                new OrganizationUser { Id = owner, OrganizationId = org, DisplayName = "CEO", PermissionLevel = OrganizationPermissionLevel.Owner },
                new OrganizationUser { Id = agent, OrganizationId = org, DisplayName = "Agent", AgentInstallationId = target.Id,
                    EmployeeType = EmployeeType.Agent, PermissionLevel = OrganizationPermissionLevel.Contributor });
            await db.SaveChangesAsync();
            var calendar = new CSweet.Infrastructure.WorkManagement.BusinessCalendarService(db, TimeProvider.System,
                new CSweet.Infrastructure.Core.EmployeeHierarchyAccessService(db),
                new CSweet.Infrastructure.WorkManagement.WorkItemMutationEngine(db, TimeProvider.System));
            var start = DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(-5), DateTimeKind.Unspecified);
            await calendar.CreateAsync(org, new(OrganizationUserId: owner),
                new(new("Scheduled report", start, start.AddHours(1), AttendeeIds: [agent], ReminderMinutes: [0],
                    Work: new("Instructions", agent, "Prepare report")), "runtime"), default);
            await calendar.DispatchDueAsync(default);
        }
        var dispatcher = new AgentPlatformEventDispatcher(provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System, NullLogger<AgentPlatformEventDispatcher>.Instance);
        await dispatcher.DispatchPendingAsync(default);
        await dispatcher.DispatchPendingAsync(default);
        await using var verify = provider.CreateAsyncScope();
        var result = verify.ServiceProvider.GetRequiredService<CSweetDbContext>();
        var inbox = await result.AgentWorkItems.ToListAsync();
        Assert.Equal(2, inbox.Count);
        Assert.All(inbox, item => Assert.Equal(target.Id, item.AgentInstallationId));
        Assert.Single(inbox, item => item.Name == events[0]);
        Assert.Single(inbox, item => item.Name == events[1]);
        Assert.Contains(target.Id, runtime.QueuedInstallationIds);
        Assert.DoesNotContain(other.Id, runtime.QueuedInstallationIds);
        Assert.All(await result.AgentPlatformEventOutbox.ToListAsync(), item => Assert.Equal(AgentPlatformEventOutboxStatus.Published, item.Status));
    }
    private static AgentInstallation Installation(Guid organizationId, IReadOnlyList<string> subscriptions)
    {
        var installation = new AgentInstallation
        {
            Id = Guid.NewGuid(),
            InstallationKey = Guid.NewGuid(),
            PackageVersionId = Guid.NewGuid(),
            BusinessId = organizationId.ToString("D"),
            IsEnabled = true,
            RevisionStatus = PluginRevisionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        installation.Grant = new AgentInstallationGrant
        {
            Id = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            EventSubscriptionsJson = JsonSerializer.Serialize(subscriptions),
            ApprovedAt = DateTimeOffset.UtcNow
        };
        return installation;
    }

    private sealed class RecordingRuntimeManager : IAgentRuntimeManager
    {
        public List<Guid> QueuedInstallationIds { get; } = [];
        public bool FailNext { get; init; }
        private bool _hasFailed;

        public Task<bool> EnsureRuntimeQueuedAsync(
            Guid installationId,
            string reason,
            bool interactive = false,
            CancellationToken cancellationToken = default)
        {
            if (FailNext && !_hasFailed)
            {
                _hasFailed = true;
                throw new InvalidOperationException("Runtime queue unavailable.");
            }
            QueuedInstallationIds.Add(installationId);
            return Task.FromResult(true);
        }

        public Task<bool> RestartRuntimeAsync(Guid installationId, string reason, bool interactive = false,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<int> EnsureAlwaysOnRuntimesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<int> ProcessDueSchedulesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<int> ReconcileAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
