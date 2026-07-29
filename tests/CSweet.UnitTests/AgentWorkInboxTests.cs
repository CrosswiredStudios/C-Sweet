using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CSweet.UnitTests;

public sealed class AgentWorkInboxTests
{
    [Fact]
    public async Task EnqueueAsync_IsIdempotentOnlyForIdenticalContent()
    {
        await using var db = CreateDb();
        var installation = new AgentInstallation
        {
            Id = Guid.NewGuid(),
            InstallationKey = Guid.NewGuid(),
            PackageVersionId = Guid.NewGuid(),
            BusinessId = "organization-1",
            IsEnabled = true,
            RevisionStatus = PluginRevisionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.AgentInstallations.Add(installation);
        await db.SaveChangesAsync();
        var inbox = new AgentWorkInbox(
            db,
            new EphemeralDataProtectionProvider(),
            TimeProvider.System);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);

        var first = await inbox.EnqueueAsync(
            installation.BusinessId,
            installation.Id,
            AgentWorkKind.Capability,
            "example.execute.v1",
            Json("""{"value":1}"""),
            "same-key",
            deadline);
        var duplicate = await inbox.EnqueueAsync(
            installation.BusinessId,
            installation.Id,
            AgentWorkKind.Capability,
            "example.execute.v1",
            Json("""{"value":1}"""),
            "same-key",
            deadline);

        Assert.Equal(first.Id, duplicate.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => inbox.EnqueueAsync(
            installation.BusinessId,
            installation.Id,
            AgentWorkKind.Capability,
            "example.execute.v1",
            Json("""{"value":2}"""),
            "same-key",
            deadline));
    }

    [Fact]
    public async Task EnqueueAsync_RejectsEventWorkWithoutStableEventIdentity()
    {
        await using var db = CreateDb();
        var installation = Installation(DateTimeOffset.UtcNow);
        db.Add(installation);
        await db.SaveChangesAsync();
        var inbox = new AgentWorkInbox(
            db,
            new EphemeralDataProtectionProvider(),
            TimeProvider.System);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            inbox.EnqueueAsync(
                installation.BusinessId,
                installation.Id,
                AgentWorkKind.Event,
                "example.event.v1",
                Json("{}"),
                "missing-event-id",
                DateTimeOffset.UtcNow.AddMinutes(5)));

        Assert.Contains("originating event ID", exception.Message, StringComparison.Ordinal);

        var firstEventId = Guid.NewGuid();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        _ = await inbox.EnqueueAsync(
            installation.BusinessId,
            installation.Id,
            AgentWorkKind.Event,
            "example.event.v1",
            Json("{}"),
            "stable-event",
            deadline,
            sourceId: firstEventId.ToString("D"));
        _ = await inbox.EnqueueAsync(
            installation.BusinessId,
            installation.Id,
            AgentWorkKind.Event,
            "example.event.v1",
            Json("{}"),
            "stable-event",
            deadline,
            sourceId: firstEventId.ToString("D"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            inbox.EnqueueAsync(
                installation.BusinessId,
                installation.Id,
                AgentWorkKind.Event,
                "example.event.v1",
                Json("{}"),
                "stable-event",
                deadline,
                sourceId: Guid.NewGuid().ToString("D")));
    }

    [Fact]
    public async Task Lease_RejectsForgeryStalenessAndConflictingCompletion()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        await using var db = CreateDb();
        var installation = Installation(clock.GetUtcNow());
        var runtime = new AgentRuntimeInstance
        {
            Id = Guid.NewGuid(),
            TickId = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            QueuedAt = clock.GetUtcNow(),
            RuntimeDeadlineAt = clock.GetUtcNow().AddMinutes(10)
        };
        db.AddRange(installation, runtime);
        await db.SaveChangesAsync();
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), clock);
        var session = new McpAgentSession
        {
            Id = Guid.NewGuid(),
            RuntimeInstanceId = runtime.Id,
            TickId = runtime.TickId,
            AgentInstallationId = installation.Id,
            OrganizationId = installation.BusinessId
        };
        var item = await inbox.EnqueueAsync(
            installation.BusinessId,
            installation.Id,
            AgentWorkKind.Capability,
            "example.execute.v1",
            Json("""{"value":1}"""),
            "lease-test",
            clock.GetUtcNow().AddMinutes(5));
        var claimed = await inbox.ClaimAsync(session, CancellationToken.None);
        Assert.NotNull(claimed);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inbox.RenewAsync(
            session, item.Id, claimed!.Attempt, "forged", CancellationToken.None));
        await inbox.AppendProgressAsync(
            session, item.Id, claimed.Attempt, claimed.LeaseToken, 1,
            Json("""{"stage":"one"}"""), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => inbox.AppendProgressAsync(
            session, item.Id, claimed.Attempt, claimed.LeaseToken, 3,
            Json("""{"stage":"gap"}"""), CancellationToken.None));

        var success = new AgentWorkCompletion(true, Json("""{"ok":true}"""), null);
        await inbox.CompleteAsync(
            session, item.Id, claimed.Attempt, claimed.LeaseToken, success, CancellationToken.None);
        await inbox.CompleteAsync(
            session, item.Id, claimed.Attempt, claimed.LeaseToken, success, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => inbox.CompleteAsync(
            session, item.Id, claimed.Attempt, claimed.LeaseToken,
            new AgentWorkCompletion(true, Json("""{"ok":false}"""), null),
            CancellationToken.None));
    }

    [Fact]
    public async Task Lease_ExpiresAfterSixtySeconds()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        await using var db = CreateDb();
        var installation = Installation(clock.GetUtcNow());
        var runtime = new AgentRuntimeInstance
        {
            Id = Guid.NewGuid(), TickId = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            QueuedAt = clock.GetUtcNow()
        };
        db.AddRange(installation, runtime);
        await db.SaveChangesAsync();
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), clock);
        var session = new McpAgentSession
        {
            Id = Guid.NewGuid(), RuntimeInstanceId = runtime.Id, TickId = runtime.TickId,
            AgentInstallationId = installation.Id, OrganizationId = installation.BusinessId
        };
        var eventId = Guid.NewGuid();
        await inbox.EnqueueAsync(
            installation.BusinessId, installation.Id, AgentWorkKind.Event, "example.event.v1",
            Json("{}"), "expiry-test", clock.GetUtcNow().AddMinutes(5),
            sourceId: eventId.ToString("D"));
        var claimed = await inbox.ClaimAsync(session, CancellationToken.None);
        Assert.NotNull(claimed);
        Assert.Equal(eventId, claimed.EventId);
        Assert.NotEqual(claimed.WorkId, claimed.EventId);

        clock.Advance(TimeSpan.FromSeconds(61));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inbox.RenewAsync(
            session, claimed!.WorkId, claimed.Attempt, claimed.LeaseToken, CancellationToken.None));
    }

    [Fact]
    public async Task ClaimAsync_DeadLettersPendingWorkWhoseDeadlineElapsed()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        await using var db = CreateDb();
        var installation = Installation(clock.GetUtcNow());
        var runtime = new AgentRuntimeInstance
        {
            Id = Guid.NewGuid(),
            TickId = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            QueuedAt = clock.GetUtcNow()
        };
        db.AddRange(installation, runtime);
        await db.SaveChangesAsync();
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), clock);
        var session = new McpAgentSession
        {
            Id = Guid.NewGuid(),
            RuntimeInstanceId = runtime.Id,
            TickId = runtime.TickId,
            AgentInstallationId = installation.Id,
            OrganizationId = installation.BusinessId
        };
        var item = await inbox.EnqueueAsync(
            installation.BusinessId,
            installation.Id,
            AgentWorkKind.Capability,
            "agent.configuration.update.v1",
            Json("{}"),
            "expired-pending",
            clock.GetUtcNow().AddSeconds(5));

        clock.Advance(TimeSpan.FromSeconds(6));

        Assert.Null(await inbox.ClaimAsync(session, CancellationToken.None));
        db.ChangeTracker.Clear();
        var expired = await db.AgentWorkItems.SingleAsync(x => x.Id == item.Id);
        Assert.Equal(AgentWorkStatus.DeadLetter, expired.Status);
        Assert.Contains("deadline elapsed", expired.LastError, StringComparison.OrdinalIgnoreCase);
    }

    private static CSweetDbContext CreateDb() => new(
        new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static JsonElement Json(string value) =>
        JsonDocument.Parse(value).RootElement.Clone();

    private static AgentInstallation Installation(DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        InstallationKey = Guid.NewGuid(),
        PackageVersionId = Guid.NewGuid(),
        BusinessId = "organization-1",
        IsEnabled = true,
        RevisionStatus = PluginRevisionStatus.Active,
        CreatedAt = now,
        UpdatedAt = now
    };

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
