using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentWorkInboxTests
{
    [Fact]
    public async Task WorkHeldByALostRuntimeIsRequeuedAtOnceInsteadOfWaitingOutItsLease()
    {
        // Super Awesome Games ticket 34 (2026-11-13): the Office let a runtime's VM assignment lapse, C-Sweet retired
        // the runtime at 04:30:29, and its ticket attempt sat until the three-minute work lease ran out at 04:33:04.
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var installation = Installation(clock.GetUtcNow());
        var original = Runtime(installation, clock.GetUtcNow());
        db.AddRange(installation, original);
        await db.SaveChangesAsync();
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), clock);
        var work = await inbox.EnqueueAsync(installation.BusinessId, installation.Id, AgentWorkKind.Capability,
            "example.execute.v1", Json("{}"), "runtime-lost", clock.GetUtcNow().AddHours(1));
        var first = (await inbox.ClaimAsync(Session(installation, original), default))!;
        clock.Advance(TimeSpan.FromSeconds(10)); // well inside the work lease

        // While the old runtime is only stopping it may still be finishing; nothing is taken from it.
        original.TransitionTo(AgentRuntimeStatus.Stopping, clock.GetUtcNow());
        var replacement = Runtime(installation, clock.GetUtcNow());
        db.Add(replacement);
        await db.SaveChangesAsync();
        var nextSession = Session(installation, replacement);
        Assert.Null(await inbox.ClaimAsync(nextSession, default));
        Assert.Equal(AgentWorkStatus.Leased, work.Status);

        original.TransitionTo(AgentRuntimeStatus.Failed, clock.GetUtcNow(),
            "The runtime assignment lease expired. The runtime must be replaced with a fresh identity before durable work can resume.");
        await db.SaveChangesAsync();
        Assert.Null(await inbox.ClaimAsync(nextSession, default)); // settles the lost attempt
        Assert.Equal(AgentWorkStatus.Pending, work.Status);
        var resumed = (await inbox.ClaimAsync(nextSession, default))!;
        Assert.Equal(work.Id, resumed.WorkId);
        Assert.Equal(first.Attempt + 1, resumed.Attempt);
        Assert.Equal("lease_expired", (await db.AgentWorkAttempts.SingleAsync(x => x.Attempt == first.Attempt)).Error);
    }

    [Fact]
    public async Task FreshRuntimeResumesTheSameDurableWorkAndRejectsLateOldRuntimeEffects()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var installation = Installation(clock.GetUtcNow());
        var original = Runtime(installation, clock.GetUtcNow());
        db.AddRange(installation, original);
        await db.SaveChangesAsync();
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), clock);
        var work = await inbox.EnqueueAsync(installation.BusinessId, installation.Id, AgentWorkKind.Capability,
            "example.execute.v1", Json("{\"savedWork\":\"checkpoint-123\"}"), "runtime-recovery", clock.GetUtcNow().AddHours(1));
        var oldSession = Session(installation, original);
        var first = (await inbox.ClaimAsync(oldSession, default))!;
        Assert.NotNull(first);
        original.TransitionTo(AgentRuntimeStatus.Stopping, clock.GetUtcNow());
        original.TransitionTo(AgentRuntimeStatus.Failed, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(3).Add(TimeSpan.FromSeconds(1)));
        var replacement = Runtime(installation, clock.GetUtcNow());
        db.Add(replacement);
        await db.SaveChangesAsync();

        var nextSession = Session(installation, replacement);
        // The first discovery durably settles expired leases; the next pickup sees the
        // newly Pending row. No task body or completion is replayed during that settlement.
        Assert.Null(await inbox.ClaimAsync(nextSession, default));
        Assert.Equal(AgentWorkStatus.Pending, work.Status);
        var resumed = (await inbox.ClaimAsync(nextSession, default))!;
        Assert.NotNull(resumed);
        Assert.Equal(work.Id, resumed.WorkId);
        Assert.Equal(first.Attempt + 1, resumed.Attempt);
        Assert.Equal(first.Payload.GetRawText(), resumed.Payload.GetRawText());
        Assert.NotEqual(first.LeaseToken, resumed.LeaseToken);
        var attempts = await db.AgentWorkAttempts.OrderBy(x => x.Attempt).ToArrayAsync();
        Assert.Equal("lease_expired", attempts[0].Error);
        Assert.NotNull(attempts[0].FinishedAt);
        Assert.Equal(replacement.Id, attempts[1].RuntimeInstanceId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inbox.RenewAsync(oldSession,
            first.WorkId, first.Attempt, first.LeaseToken, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => inbox.CompleteAsync(oldSession,
            first.WorkId, first.Attempt, first.LeaseToken, new(true, Json("{}"), null), default));
        await inbox.CompleteAsync(nextSession, resumed.WorkId, resumed.Attempt, resumed.LeaseToken,
            new(true, Json("{\"completed\":true}"), null), default);
        Assert.Equal(AgentWorkStatus.Completed, work.Status);
        Assert.Null(await inbox.ClaimAsync(nextSession, default));
    }
}
