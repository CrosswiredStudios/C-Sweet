using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RetainedContextPickupKeepsActiveChatAliveThenRotatesBeforeIncompatibleWork(bool recallMemory, bool otherHuman)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture);
        var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context();
        await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        var (inbox, work) = await QueueRecallAsync(fixture, db, turn, recallMemory);
        var claim = Assert.IsType<ClaimedAgentWork>(await inbox.ClaimAsync(DeliverySession(session), default));
        var persisted = DeliverySession(session);
        persisted.PackageVersionId = (await db.AgentInstallations.SingleAsync()).PackageVersionId;
        db.McpAgentSessions.Add(persisted);
        await db.SaveChangesAsync();
        AgentWorkItem next;
        if (otherHuman)
        {
            var human = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, EmployeeType = EmployeeType.Human };
            var conversation = new Conversation { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
                AgentOrganizationUserId = fixture.EmployeeId, InitiatedByOrganizationUserId = human.Id };
            var message = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = conversation.Id,
                Role = ConversationRole.User, SenderOrganizationUserId = human.Id, Content = "A different human" };
            var nextTurn = new ChatTurn { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, ConversationId = conversation.Id,
                TargetAgentOrganizationUserId = fixture.EmployeeId, UserMessageId = message.Id, Status = ChatTurnStatus.RecallingMemory };
            db.AddRange(human, conversation, message, nextTurn);
            await db.SaveChangesAsync();
            var prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(nextTurn.Id, false);
            next = await inbox.EnqueueAsync(session.BusinessId, fixture.InstallationId, AgentWorkKind.Event, "user-message",
                RecallPayload(prepared, nextTurn), Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddMinutes(10),
                sourceType: "chat-turn", sourceId: nextTurn.Id.ToString("D"), memoryRecallReceiptJson: prepared.ReceiptJson);
        }
        else next = await inbox.EnqueueAsync(session.BusinessId, fixture.InstallationId, AgentWorkKind.Event, "coordination-turn",
            JsonSerializer.SerializeToElement(new { }), Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddMinutes(10),
            sourceType: "agent-coordination", sourceId: Guid.NewGuid().ToString("D"));

        // Repeated pickup and a fresh service/session see the same durable running lease.
        Assert.Null(await inbox.ClaimAsync(DeliverySession(session), default));
        Assert.Null(await inbox.ClaimAsync(DeliverySession(session), default));
        await using (var reconnected = fixture.Context())
            Assert.False(await new MemoryRecallDispatchEvidence(reconnected).RequireRetainedConsumerAsync(DeliverySession(session), next, default));
        await using (var current = fixture.Context())
        {
            Assert.Null((await current.AgentRuntimeInstances.SingleAsync()).MemoryResetRequestedAt);
            Assert.Null((await current.McpAgentSessions.SingleAsync()).RevokedAt);
            Assert.Null((await current.AgentWorkAttempts.SingleAsync()).FinishedAt);
            Assert.Equal(AgentWorkStatus.Leased, (await current.AgentWorkItems.SingleAsync(x => x.Id == work.Id)).Status);
            Assert.Equal(0, (await current.AgentWorkItems.SingleAsync(x => x.Id == next.Id)).AttemptCount);
            Assert.Empty(await current.AgentWorkAttempts.Where(x => x.AgentWorkItemId == next.Id).ToArrayAsync());
            Assert.Empty(await current.AgentRuntimeEvents.ToArrayAsync());
        }
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
        await inbox.AppendProgressAsync(DeliverySession(session), work.Id, claim.Attempt, claim.LeaseToken, 1,
            JsonSerializer.SerializeToElement(new { delta = "The approval has been created." }), default);
        var completion = new AgentWorkCompletion(true, JsonSerializer.SerializeToElement(new { reply = "Pitch ready", approvalId = Guid.NewGuid() }), null);
        await inbox.CompleteAsync(DeliverySession(session), work.Id, claim.Attempt, claim.LeaseToken, completion, default);
        await inbox.CompleteAsync(DeliverySession(session), work.Id, claim.Attempt, claim.LeaseToken, completion, default);
        Assert.Null(await inbox.ClaimAsync(DeliverySession(session), default));
        await inbox.SettleMemoryResetAsync(Guid.Parse(session.RuntimeInstanceId), default);
        Assert.True((await inbox.ReadStateAsync(work.Id, default)).Completion!.Succeeded);
        Assert.Equal(AgentWorkStatus.Pending, (await inbox.ReadStateAsync(next.Id, default)).Status);
        Assert.Equal(1, (await db.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == work.Id)).AttemptCount);
        Assert.NotNull((await db.McpAgentSessions.AsNoTracking().SingleAsync()).RevokedAt);
        var diagnostic = Assert.Single(await db.AgentRuntimeEvents.AsNoTracking().ToArrayAsync()).Reason;
        Assert.Contains(otherHuman ? "queued-recall.consumer-audience" : "queued-recall.consumer-kind", diagnostic);

        // A clean replacement may claim the pending work once, without inheriting old receipts.
        // Simulate confirmed shutdown here; AgentRuntimeManagerTests separately exercises provider
        // confirmation and prevents replacement while shutdown is uncertain.
        var retired = await db.AgentRuntimeInstances.SingleAsync();
        retired.TransitionTo(AgentRuntimeStatus.Stopping, DateTimeOffset.UtcNow);
        retired.TransitionTo(AgentRuntimeStatus.Cancelled, DateTimeOffset.UtcNow);
        retired.MemoryResetCompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var replacement = new AgentRuntimeInstance { Id = Guid.NewGuid(), AgentInstallationId = fixture.InstallationId,
            TickId = Guid.NewGuid(), RuntimeDeadlineAt = DateTimeOffset.UtcNow.AddHours(1) };
        replacement.TransitionTo(AgentRuntimeStatus.Starting, DateTimeOffset.UtcNow);
        replacement.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, DateTimeOffset.UtcNow);
        replacement.TransitionTo(AgentRuntimeStatus.Running, DateTimeOffset.UtcNow);
        db.AgentRuntimeInstances.Add(replacement); await db.SaveChangesAsync();
        var replacementSession = DeliverySession(session);
        replacementSession.RuntimeInstanceId = replacement.Id; replacementSession.TickId = replacement.TickId;
        Assert.Equal(next.Id, (await inbox.ClaimAsync(replacementSession, default))!.WorkId);
        Assert.Equal(1, await db.AgentWorkAttempts.CountAsync(x => x.AgentWorkItemId == next.Id));
        Assert.Equal(1, (await db.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == work.Id)).AttemptCount);
    }

    [MemoryPostgresFact]
    public async Task RetainedContextPickupAlsoSerializesCompatibleChatsWithoutResettingAfterCompletion()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture); var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context();
        await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        var (inbox, work) = await QueueRecallAsync(fixture, db, turn);
        var claim = Assert.IsType<ClaimedAgentWork>(await inbox.ClaimAsync(DeliverySession(session), default));
        var nextTurn = await SeedRecallTurnAsync(fixture);
        var (nextInbox, nextWork) = await QueueRecallAsync(fixture, db, nextTurn, includeMemory: false);
        Assert.Null(await nextInbox.ClaimAsync(DeliverySession(session), default));
        Assert.Null((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
        await inbox.CompleteAsync(DeliverySession(session), work.Id, claim.Attempt, claim.LeaseToken,
            new AgentWorkCompletion(true, JsonSerializer.SerializeToElement(new { reply = "First reply complete" }), null), default);
        Assert.Equal(nextWork.Id, (await nextInbox.ClaimAsync(DeliverySession(session), default))!.WorkId);
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, nextWork.Id, default);
        Assert.Null((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
    }

    [MemoryPostgresTheory]
    [InlineData("source")]
    [InlineData("authority")]
    [InlineData("capacity")]
    [InlineData("legacy")]
    public async Task RetainedContextPickupStillImmediatelyFencesUnsafeActiveChat(string change)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture); var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context();
        await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        var (inbox, work) = await QueueRecallAsync(fixture, db, turn);
        Assert.NotNull(await inbox.ClaimAsync(DeliverySession(session), default));
        if (change == "source")
            await db.CoreConversationMessages.Where(x => x.Id == fixture.MessageId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Content, "Source changed"));
        else if (change == "authority")
            await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        else if (change == "legacy")
            await db.AgentRuntimeInstances.ExecuteUpdateAsync(s => s.SetProperty(x => x.MemoryReadEvidenceVersion, 0));
        else
        {
            var original = await db.AgentMemoryReadReceipts.AsNoTracking().SingleAsync();
            for (var i = 0; i < 64; i++) db.AgentMemoryReadReceipts.Add(new AgentMemoryReadReceipt { Id = Guid.NewGuid(),
                RuntimeId = original.RuntimeId, InstallationId = original.InstallationId, EmployeeId = original.EmployeeId,
                OrganizationId = original.OrganizationId, WorkId = original.WorkId, Attempt = original.Attempt,
                GrantRevision = original.GrantRevision, Capability = original.Capability, EvidenceJson = original.EvidenceJson,
                AuthorityHash = original.AuthorityHash, ReceiptHash = Guid.NewGuid().ToString("N") });
            await db.SaveChangesAsync();
        }
        var next = await inbox.EnqueueAsync(session.BusinessId, fixture.InstallationId, AgentWorkKind.Event, "coordination-turn",
            JsonSerializer.SerializeToElement(new { }), Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddMinutes(10),
            sourceType: "agent-coordination", sourceId: Guid.NewGuid().ToString("D"));
        Assert.Null(await inbox.ClaimAsync(DeliverySession(session), default));
        await using var current = fixture.Context();
        var runtime = await current.AgentRuntimeInstances.SingleAsync();
        Assert.NotNull(runtime.MemoryResetRequestedAt);
        Assert.Equal(change == "capacity" ? MemoryRuntimeResetRequiredException.ReceiptCapacity : change == "legacy" ?
            MemoryRuntimeResetRequiredException.LegacyEvidence : MemoryRuntimeResetRequiredException.RetainedEvidence, runtime.MemoryResetReasonCode);
        Assert.Equal(MemoryRuntimeResetRequiredException.FailureCode, (await current.AgentWorkAttempts.SingleAsync()).Error);
        Assert.Equal(0, (await current.AgentWorkItems.SingleAsync(x => x.Id == next.Id)).AttemptCount);
        Assert.DoesNotContain("Alice", Assert.Single(await current.AgentRuntimeEvents.ToArrayAsync()).Reason);
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedContextPickupHoldsRawReadConsumerThenFencesExpiryWithoutAutomaticReplay(bool deadline)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture);
        await SeedReviewClaim(fixture);
        await using var db = fixture.Context();
        Assert.True((await ReadHandler(fixture, db).HandleAsync(session, ReadSearch(fixture), default)).Succeeded);
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var next = await inbox.EnqueueAsync(session.BusinessId, fixture.InstallationId, AgentWorkKind.Event, "coordination-turn",
            JsonSerializer.SerializeToElement(new { }), Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddMinutes(10),
            sourceType: "agent-coordination", sourceId: Guid.NewGuid().ToString("D"));
        Assert.Null(await inbox.ClaimAsync(DeliverySession(session), default));
        Assert.Null((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
        if (deadline)
            await db.AgentWorkItems.Where(x => x.Id == work.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DeadlineAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        else await db.AgentWorkAttempts.ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Null(await inbox.ClaimAsync(DeliverySession(session), default));
        Assert.NotNull((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
        await inbox.SettleMemoryResetAsync(Guid.Parse(session.RuntimeInstanceId), default);
        Assert.Equal(AgentWorkStatus.DeadLetter, (await db.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == work.Id)).Status);
        Assert.False((await inbox.ReadStateAsync(work.Id, default)).Completion!.Retryable);
        Assert.Equal(1, (await db.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == work.Id)).AttemptCount);
        Assert.Equal(AgentWorkStatus.Pending, (await db.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == next.Id)).Status);
        Assert.Contains("validation=claim.consumer-lease-expired", Assert.Single(await db.AgentRuntimeEvents.AsNoTracking().ToArrayAsync()).Reason);
    }
}
