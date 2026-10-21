using System.Text.Json;
using CSweet.Contracts.Communications;
using CSweet.Contracts.Realtime;
using CSweet.Domain.Core;
using CSweet.Domain.Notifications;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CSweet.UnitTests;

public sealed class AgentPresenceRealtimeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RuntimeAndReadinessChangesCommitWakeHintsAndRefreshAuthoritativePresence(bool synchronous, bool relational)
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>();
        // This relational test exercises state/outbox transactions, not unrelated FK fixtures.
        if (relational) options.UseSqlite("Data Source=:memory:;Foreign Keys=False")
            .AddInterceptors(new PresenceSequenceInterceptor());
        else options.UseInMemoryDatabase(Guid.NewGuid().ToString());
        await using var db = new CSweetDbContext(options.Options);
        if (relational)
        {
            await db.Database.OpenConnectionAsync();
            await db.Database.EnsureCreatedAsync();
        }
        var org = Guid.NewGuid();
        var owner = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, IsActive = true,
            EmployeeType = EmployeeType.Human, DisplayName = "Owner" };
        var outsider = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), IsActive = true };
        var inactive = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, IsActive = false };
        var installation = new AgentInstallation { Id = Guid.NewGuid(), PackageVersionId = Guid.NewGuid(),
            InstallationKey = Guid.NewGuid(), BusinessId = org.ToString("D"), SetupState = PluginSetupState.Ready };
        var agent = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, IsActive = true,
            EmployeeType = EmployeeType.Agent, AgentInstallationId = installation.Id, AgentInstallation = installation,
            DisplayName = "Naomi Chen" };
        var schedule = new AgentSchedule { Id = Guid.NewGuid(), AgentInstallationId = installation.Id,
            AgentInstallation = installation, IsEnabled = true, ActivationMode = ActivationMode.AlwaysOn };
        installation.Schedule = schedule;
        var runtime = new AgentRuntimeInstance { Id = Guid.NewGuid(), AgentInstallationId = installation.Id,
            QueuedAt = DateTimeOffset.UtcNow, TickId = Guid.NewGuid() };
        db.AddRange(owner, outsider, inactive, agent, installation, schedule, runtime);
        async Task Save() { if (synchronous) db.SaveChanges(); else await db.SaveChangesAsync(); }
        await Save();
        var hub = new CommunicationHubService(db, new TestAuditEventWriter(), new ChatTurnService(db));
        if (!relational)
            await hub.CreateAsync(org, owner.Id, new CreateCommunicationChatRequest(null, null, true, true, [agent.Id]));
        async Task AssertPresence(string expected)
        {
            // Hub projections use PostgreSQL DateTimeOffset ordering, unsupported by SQLite.
            // Relational cases verify atomic outbox persistence and rollback instead.
            if (relational) return;
            var snapshot = Assert.IsType<CommunicationHubResponse>(await hub.GetAsync(org, owner.Id));
            Assert.Equal(expected, Assert.Single(snapshot.People, x => x.Id == agent.Id).PresenceStatus);
            Assert.Equal(expected, Assert.Single(Assert.Single(snapshot.Chats).Participants,
                x => x.OrganizationUserId == agent.Id).PresenceStatus);
        }
        await AssertPresence(CommunicationPresenceStatuses.Starting);
        var count = await db.ApplicationRealtimeOutbox.CountAsync(x => x.EventType == AppRealtimeEvents.AgentPresenceChanged);
        Assert.Equal(1, count); // One coalesced hint for all changes in this organization.
        if (relational)
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            runtime.TransitionTo(AgentRuntimeStatus.Starting, DateTimeOffset.UtcNow);
            await Save();
            Assert.Equal(count + 1, await db.ApplicationRealtimeOutbox.CountAsync(x => x.EventType == AppRealtimeEvents.AgentPresenceChanged));
            await transaction.RollbackAsync();
            await db.Entry(runtime).ReloadAsync();
            foreach (var entry in db.ChangeTracker.Entries<ApplicationRealtimeOutboxItem>().ToArray())
                entry.State = EntityState.Detached;
            Assert.Equal(AgentRuntimeStatus.Queued, runtime.Status);
            Assert.Equal(count, await db.ApplicationRealtimeOutbox.CountAsync(x => x.EventType == AppRealtimeEvents.AgentPresenceChanged));
        }
        foreach (var state in new[] { AgentRuntimeStatus.Starting, AgentRuntimeStatus.WaitingForMcpSession,
                     AgentRuntimeStatus.Running, AgentRuntimeStatus.Stopping, AgentRuntimeStatus.Completed })
        {
            runtime.TransitionTo(state, DateTimeOffset.UtcNow);
            await Save();
            Assert.Equal(++count, await db.ApplicationRealtimeOutbox.CountAsync(x => x.EventType == AppRealtimeEvents.AgentPresenceChanged));
            await AssertPresence(state == AgentRuntimeStatus.Running ? CommunicationPresenceStatuses.Available :
                state == AgentRuntimeStatus.Completed ? CommunicationPresenceStatuses.Offline : CommunicationPresenceStatuses.Starting);
        }
        runtime.LastInteractiveActivityAt = DateTimeOffset.UtcNow;
        runtime.LogExcerpt = "heartbeat";
        installation.UpdatedAt = DateTimeOffset.UtcNow;
        schedule.LastTickAt = DateTimeOffset.UtcNow;
        await Save();
        Assert.Equal(count, await db.ApplicationRealtimeOutbox.CountAsync(x => x.EventType == AppRealtimeEvents.AgentPresenceChanged));

        installation.IsEnabled = false;
        await Save();
        await AssertPresence(CommunicationPresenceStatuses.Unhealthy);
        Assert.Equal(++count, await db.ApplicationRealtimeOutbox.CountAsync(x => x.EventType == AppRealtimeEvents.AgentPresenceChanged));
        schedule.AutomaticStartSuppressedAt = DateTimeOffset.UtcNow;
        schedule.ConsecutiveStartupFailures = 3;
        await Save();
        Assert.Equal(++count, await db.ApplicationRealtimeOutbox.CountAsync(x => x.EventType == AppRealtimeEvents.AgentPresenceChanged));
        db.AgentRuntimeInstances.Remove(runtime);
        await Save();
        Assert.Equal(++count, await db.ApplicationRealtimeOutbox.CountAsync(x => x.EventType == AppRealtimeEvents.AgentPresenceChanged));
        var hints = await db.ApplicationRealtimeOutbox.Where(x => x.EventType == AppRealtimeEvents.AgentPresenceChanged).ToListAsync();
        Assert.All(hints, hint =>
        {
            Assert.Equal(org, hint.OrganizationId);
            Assert.Equal("{}", hint.DataJson);
            var recipients = JsonSerializer.Deserialize<Guid[]>(hint.RecipientOrganizationUserIdsJson)!;
            Assert.Contains(owner.Id, recipients);
            Assert.DoesNotContain(outsider.Id, recipients);
            Assert.DoesNotContain(inactive.Id, recipients);
        });
    }

    private sealed class PresenceSequenceInterceptor : SaveChangesInterceptor
    {
        private long _sequence;
        private void Assign(DbContext context)
        {
            foreach (var entry in context.ChangeTracker.Entries<ApplicationRealtimeOutboxItem>()
                         .Where(x => x.State == EntityState.Added && x.Entity.Sequence == 0))
                entry.Entity.Sequence = ++_sequence;
        }
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result)
        {
            Assign(eventData.Context!);
            return result;
        }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Assign(eventData.Context!);
            return ValueTask.FromResult(result);
        }
    }
}
