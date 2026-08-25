using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace CSweet.UnitTests;

public sealed class AgentWorkInboxTests
{
    [Fact]
    public async Task NeedsSetup_AllowsOnlyDeclaredBootstrapCallbacksFromSetupSource()
    {
        await using var db = CreateDb();
        var installation = Installation(DateTimeOffset.UtcNow);
        installation.SetupState = PluginSetupState.NeedsSetup;
        var package = new AgentPackageVersion
        {
            Id = installation.PackageVersionId,
            AgentId = "com.example.setup",
            Version = "1.0.0",
            ManifestJson = """{"provides":[{"name":"example.setup.discover.v1","riskClass":"bootstrap"}]}"""
        };
        db.AddRange(package, installation);
        await db.SaveChangesAsync();
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() => inbox.EnqueueAsync(
            installation.BusinessId, installation.Id, AgentWorkKind.Capability, "example.execute.v1",
            Json("{}"), "normal", DateTimeOffset.UtcNow.AddMinutes(1), sourceType: "plugin-setup"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => inbox.EnqueueAsync(
            installation.BusinessId, installation.Id, AgentWorkKind.Capability, "example.setup.discover.v1",
            Json("{}"), "wrong-source", DateTimeOffset.UtcNow.AddMinutes(1), sourceType: "management-api"));

        var accepted = await inbox.EnqueueAsync(installation.BusinessId, installation.Id,
            AgentWorkKind.Capability, "example.setup.discover.v1", Json("{}"), "bootstrap",
            DateTimeOffset.UtcNow.AddMinutes(1), sourceType: "plugin-setup");

        Assert.Equal("example.setup.discover.v1", accepted.Name);
    }

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
    public async Task Lease_ExpiresAfterThreeMinutes()
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

        clock.Advance(TimeSpan.FromMinutes(3).Add(TimeSpan.FromSeconds(1)));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inbox.RenewAsync(
            session, claimed!.WorkId, claimed.Attempt, claimed.LeaseToken, CancellationToken.None));
    }

    [Fact]
    public async Task Progress_ExtendsTheActiveLease()
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
        await inbox.EnqueueAsync(
            installation.BusinessId, installation.Id, AgentWorkKind.Event, "example.event.v1",
            Json("{}"), "progress-renewal-test", clock.GetUtcNow().AddMinutes(10),
            sourceId: Guid.NewGuid().ToString("D"));
        var claimed = Assert.IsType<ClaimedAgentWork>(
            await inbox.ClaimAsync(session, CancellationToken.None));

        clock.Advance(TimeSpan.FromMinutes(2));
        await inbox.AppendProgressAsync(
            session, claimed.WorkId, claimed.Attempt, claimed.LeaseToken, 1,
            Json("""{"stage":"working"}"""), CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(2));

        var renewedUntil = await inbox.RenewAsync(
            session, claimed.WorkId, claimed.Attempt, claimed.LeaseToken, CancellationToken.None);
        Assert.Equal(clock.GetUtcNow().Add(AgentWorkInbox.LeaseDuration), renewedUntil);
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

    [Fact]
    public async Task ClaimAsync_DoesNotRequeueAnotherInstallationsExpiredLease()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        await using var db = CreateDb();
        var firstInstallation = Installation(clock.GetUtcNow());
        var secondInstallation = Installation(clock.GetUtcNow());
        var firstRuntime = Runtime(firstInstallation, clock.GetUtcNow());
        var secondRuntime = Runtime(secondInstallation, clock.GetUtcNow());
        db.AddRange(firstInstallation, secondInstallation, firstRuntime, secondRuntime);
        await db.SaveChangesAsync();
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), clock);
        var secondSession = Session(secondInstallation, secondRuntime);
        var item = await inbox.EnqueueAsync(
            secondInstallation.BusinessId,
            secondInstallation.Id,
            AgentWorkKind.Capability,
            "example.execute.v1",
            Json("{}"),
            "second-installation-work",
            clock.GetUtcNow().AddMinutes(10));
        Assert.NotNull(await inbox.ClaimAsync(secondSession, CancellationToken.None));

        clock.Advance(AgentWorkInbox.LeaseDuration.Add(TimeSpan.FromSeconds(1)));

        Assert.Null(await inbox.ClaimAsync(
            Session(firstInstallation, firstRuntime),
            CancellationToken.None));
        db.ChangeTracker.Clear();
        var unchanged = await db.AgentWorkItems
            .Include(x => x.Attempts)
            .SingleAsync(x => x.Id == item.Id);
        Assert.Equal(AgentWorkStatus.Leased, unchanged.Status);
        Assert.Null(Assert.Single(unchanged.Attempts).FinishedAt);
    }

    [Fact]
    public async Task ClaimAsync_ConcurrentSessionsLeaseAnItemExactlyOnce()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var databaseName = Guid.NewGuid().ToString("N");
        var databaseRoot = new InMemoryDatabaseRoot();
        var protection = new EphemeralDataProtectionProvider();
        var installation = Installation(clock.GetUtcNow());
        var firstRuntime = Runtime(installation, clock.GetUtcNow());
        var secondRuntime = Runtime(installation, clock.GetUtcNow());
        await using (var seedDb = CreateDb(databaseName, databaseRoot))
        {
            seedDb.AddRange(installation, firstRuntime, secondRuntime);
            await seedDb.SaveChangesAsync();
            var seedInbox = new AgentWorkInbox(seedDb, protection, clock);
            await seedInbox.EnqueueAsync(
                installation.BusinessId,
                installation.Id,
                AgentWorkKind.Capability,
                "example.execute.v1",
                Json("{}"),
                "concurrent-claim",
                clock.GetUtcNow().AddMinutes(10));
        }

        await using var firstDb = CreateDb(databaseName, databaseRoot);
        await using var secondDb = CreateDb(databaseName, databaseRoot);
        var claims = await Task.WhenAll(
            new AgentWorkInbox(firstDb, protection, clock).ClaimAsync(
                Session(installation, firstRuntime), CancellationToken.None),
            new AgentWorkInbox(secondDb, protection, clock).ClaimAsync(
                Session(installation, secondRuntime), CancellationToken.None));

        Assert.Single(claims, x => x is not null);
        Assert.Single(claims, x => x is null);
        await using var verificationDb = CreateDb(databaseName, databaseRoot);
        Assert.Equal(1, await verificationDb.AgentWorkAttempts.CountAsync());
    }

    [Fact]
    public async Task CancelBySourceAsync_CancelsEveryActiveAttemptForTheTurn()
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
        var turnId = Guid.NewGuid();
        var item = await inbox.EnqueueAsync(
            installation.BusinessId, installation.Id, AgentWorkKind.Event, "example.event.v1",
            Json("{}"), "cancel-by-source", clock.GetUtcNow().AddMinutes(5),
            sourceType: "chat-turn", sourceId: turnId.ToString("D"));
        Assert.NotNull(await inbox.ClaimAsync(session, CancellationToken.None));

        var cancelled = await inbox.CancelBySourceAsync(
            "chat-turn", turnId.ToString("D"), "Stopped by user.");

        Assert.Equal(1, cancelled);
        db.ChangeTracker.Clear();
        var stored = await db.AgentWorkItems.Include(x => x.Attempts).SingleAsync(x => x.Id == item.Id);
        Assert.Equal(AgentWorkStatus.Cancelled, stored.Status);
        Assert.Equal("Stopped by user.", stored.LastError);
        Assert.All(stored.Attempts, x =>
        {
            Assert.NotNull(x.FinishedAt);
            Assert.Equal("cancelled", x.Error);
        });
    }

    private static CSweetDbContext CreateDb() => new(
        new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static CSweetDbContext CreateDb(string databaseName, InMemoryDatabaseRoot databaseRoot) => new(
        new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(databaseName, databaseRoot)
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

    private static AgentRuntimeInstance Runtime(AgentInstallation installation, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        TickId = Guid.NewGuid(),
        AgentInstallationId = installation.Id,
        QueuedAt = now,
        RuntimeDeadlineAt = now.AddMinutes(30)
    };

    private static McpAgentSession Session(
        AgentInstallation installation,
        AgentRuntimeInstance runtime) => new()
    {
        Id = Guid.NewGuid(),
        RuntimeInstanceId = runtime.Id,
        TickId = runtime.TickId,
        AgentInstallationId = installation.Id,
        OrganizationId = installation.BusinessId
    };

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
