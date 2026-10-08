using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private static McpAgentSession DeliverySession(AgentSession session) => new()
    {
        Id = Guid.NewGuid(), OrganizationId = session.BusinessId, AgentInstallationId = Guid.Parse(session.InstallationId),
        RuntimeInstanceId = Guid.Parse(session.RuntimeInstanceId), TickId = Guid.Parse(session.TickId), GrantRevision = session.Grant.Revision
    };

    private static async Task<(AgentWorkInbox Inbox, AgentWorkItem Work)> QueueRecallAsync(DurabilityFixture fixture,
        CSweetDbContext db, ChatTurn turn, bool includeMemory = true)
    {
        await fixture.Service(db, new UsageProviderFactory()).CaptureMessageAsync(turn.UserMessageId);
        var prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id, includeMemory);
        if (includeMemory) Assert.Contains("Alice", prepared.Context);
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var work = await inbox.EnqueueAsync(fixture.OrganizationId.ToString("D"), fixture.InstallationId, AgentWorkKind.Event, "user-message",
            RecallPayload(prepared, turn), Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddMinutes(10),
            sourceType: "chat-turn", sourceId: turn.Id.ToString("D"), memoryRecallReceiptJson: prepared.ReceiptJson);
        return (inbox, work);
    }

    [MemoryPostgresFact]
    public async Task QueuedRecallResetRecordsTheSpecificNonChatConsumerValidation()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture);
        var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context();
        await db.Database.OpenConnectionAsync();
        await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        var (inbox, work) = await QueueRecallAsync(fixture, db, turn, includeMemory: false);
        Assert.NotNull(await inbox.ClaimAsync(DeliverySession(session), default));
        var receipt = Assert.Single(await db.AgentMemoryReadReceipts.ToListAsync());
        await db.AgentWorkItems.Where(x => x.Id == work.Id).ExecuteDeleteAsync();
        await db.ChatTurns.Where(x => x.Id == turn.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ChatTurnStatus.Completed));
        var next = await inbox.EnqueueAsync(session.BusinessId, fixture.InstallationId, AgentWorkKind.Event,
            "coordination-turn", JsonSerializer.SerializeToElement(new { }), "coordination-after-chat",
            DateTimeOffset.UtcNow.AddMinutes(10), sourceType: "agent-coordination", sourceId: Guid.NewGuid().ToString("D"));
        // Private chat context must not reach agent coordination. The runtime is rotated before the
        // coordination turn is delivered, so the turn stays pending for a fresh runtime instead of failing.
        Assert.Null(await inbox.ClaimAsync(DeliverySession(session), default));
        await using var fresh = fixture.Context();
        var pending = await fresh.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == next.Id);
        Assert.Equal(AgentWorkStatus.Pending, pending.Status);
        Assert.Equal(0, pending.AttemptCount);
        Assert.Empty(await fresh.AgentWorkAttempts.Where(x => x.AgentWorkItemId == next.Id).ToListAsync());
        var runtimeId = Guid.Parse(session.RuntimeInstanceId);
        Assert.Equal(MemoryRuntimeResetRequiredException.RetainedEvidence,
            (await fresh.AgentRuntimeInstances.SingleAsync(x => x.Id == runtimeId)).MemoryResetReasonCode);
        var diagnostic = Assert.Single(await fresh.AgentRuntimeEvents.ToListAsync()).Reason;
        Assert.Contains("validation=claim.queued-recall.consumer-kind", diagnostic);
        Assert.Contains($"receipt={receipt.Id:D}", diagnostic);
        Assert.Contains($"work={next.Id:D}", diagnostic);
        Assert.DoesNotContain("Alice", diagnostic);
    }

    [MemoryPostgresFact]
    public async Task QueuedMemoryDeliveryRetainsEvidenceAfterWorkDeletionAndTurnCompletion()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture); var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        var (inbox, work) = await QueueRecallAsync(fixture, db, turn);
        var claimed = await inbox.ClaimAsync(DeliverySession(session), default);
        Assert.NotNull(claimed); Assert.Equal(work.Id, claimed.WorkId);
        var delivered = Assert.Single(await db.AgentMemoryReadReceipts.AsNoTracking().ToListAsync());
        Assert.Equal(MemoryRecallDispatchEvidence.QueuedRecallCapability, delivered.Capability);
        Assert.Equal(claimed.Attempt, delivered.Attempt); Assert.DoesNotContain("Alice", delivered.EvidenceJson);
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
        await db.AgentWorkItems.Where(x => x.Id == work.Id).ExecuteDeleteAsync();
        await db.ChatTurns.Where(x => x.Id == turn.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ChatTurnStatus.Completed));
        var nextTurn = await SeedRecallTurnAsync(fixture);
        var (nextInbox, nextWork) = await QueueRecallAsync(fixture, db, nextTurn, includeMemory: false);
        Assert.NotNull(await nextInbox.ClaimAsync(DeliverySession(session), default));
        await using var fresh = fixture.Context(); await fresh.Database.OpenConnectionAsync();
        await new PlatformMemoryReadEvidence(fresh).AuthorizeDispatchAsync(session, nextWork.Id, default);
        Assert.Equal(2, await fresh.AgentMemoryReadReceipts.CountAsync());
        await db.CoreConversationMessages.Where(x => x.Id == fixture.MessageId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Content, "Changed after the prior job finished"));
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(fresh).AuthorizeDispatchAsync(session, nextWork.Id, default));
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(fresh).AuthorizeDispatchAsync(session, null, default));
    }

    [MemoryPostgresFact]
    public async Task QueuedMemoryDeliveryFailureRollsBackReceiptAndLeaseTogether()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture); var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context(new ReadReceiptSaveFailure()); await db.Database.OpenConnectionAsync();
        await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        var (inbox, work) = await QueueRecallAsync(fixture, db, turn);
        await Assert.ThrowsAsync<InvalidOperationException>(() => inbox.ClaimAsync(DeliverySession(session), default));
        await using var fresh = fixture.Context();
        Assert.Empty(await fresh.AgentMemoryReadReceipts.ToListAsync()); Assert.Empty(await fresh.AgentWorkAttempts.ToListAsync());
        var current = await fresh.AgentWorkItems.SingleAsync(x => x.Id == work.Id);
        Assert.Equal(AgentWorkStatus.Pending, current.Status); Assert.Equal(0, current.AttemptCount);
    }

    [MemoryPostgresFact]
    public async Task QueuedMemoryDeliveryRejectsStaleEvidenceBeforeReturningPayload()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture); var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        var (inbox, work) = await QueueRecallAsync(fixture, db, turn);
        var next = await inbox.EnqueueAsync(fixture.OrganizationId.ToString("D"), fixture.InstallationId, AgentWorkKind.Event, "next-event",
            JsonSerializer.SerializeToElement(new { text = "unrelated work" }), "next-event", DateTimeOffset.UtcNow.AddMinutes(10),
            sourceId: Guid.NewGuid().ToString("D"));
        await db.CoreConversationMessages.Where(x => x.Id == fixture.MessageId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Content, "Revoked before claim"));
        Assert.Null(await inbox.ClaimAsync(DeliverySession(session), default));
        Assert.Empty(await db.AgentMemoryReadReceipts.ToListAsync()); Assert.Empty(await db.AgentWorkAttempts.ToListAsync());
        var state = await inbox.ReadStateAsync(work.Id, default);
        Assert.Equal(AgentWorkStatus.DeadLetter, state.Status);
        Assert.Equal(MemoryRecallDeliveryRejectedException.Code, state.Completion?.FailureCode);
        Assert.False(state.Completion?.Retryable); Assert.DoesNotContain("Alice", state.Error);
        var failure = await Assert.ThrowsAsync<AgentWorkReportedFailureException>(() => inbox.WaitForResultAsync<object>(work.Id, TimeSpan.FromMilliseconds(1), default));
        Assert.Equal(MemoryRecallDeliveryRejectedException.Code, failure.FailureCode); Assert.False(failure.Retryable);
        Assert.Equal(next.Id, (await inbox.ClaimAsync(DeliverySession(session), default))?.WorkId);
    }

    [MemoryPostgresFact]
    public async Task QueuedMemoryDeliveryRejectsLegacyRuntimeEvenWithoutMemoryCapabilityGrant()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture); var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        await db.AgentInstallationGrants.ExecuteUpdateAsync(s => s.SetProperty(x => x.RequiredCapabilitiesJson, "[\"platform.llm.chat-stream.v1\"]"));
        var (inbox, _) = await QueueRecallAsync(fixture, db, turn);
        await db.AgentRuntimeInstances.ExecuteUpdateAsync(s => s.SetProperty(x => x.MemoryReadEvidenceVersion, 0));
        Assert.Null(await inbox.ClaimAsync(DeliverySession(session), default));
        Assert.Equal(MemoryRuntimeResetRequiredException.LegacyEvidence, (await db.AgentRuntimeInstances.SingleAsync()).MemoryResetReasonCode);
        var modelOnly = session with { Grant = session.Grant with { RequiredCapabilities = new HashSet<string>([CSweet.Agent.SDK.PlatformCapabilities.LlmChatStream]) } };
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(modelOnly, null, default));
        Assert.Empty(await db.AgentMemoryReadReceipts.ToListAsync()); Assert.Empty(await db.AgentWorkAttempts.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task QueuedMemoryDeliveryCannotClaimLegacyChatWithoutReceipt()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture); var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        await inbox.EnqueueAsync(fixture.OrganizationId.ToString("D"), fixture.InstallationId, AgentWorkKind.Event, "user-message",
            JsonSerializer.SerializeToElement(new { context = "legacy private context" }), "legacy-chat", DateTimeOffset.UtcNow.AddMinutes(10),
            sourceType: "chat-turn", sourceId: turn.Id.ToString("D"));
        Assert.Null(await inbox.ClaimAsync(DeliverySession(session), default));
        Assert.Empty(await db.AgentMemoryReadReceipts.ToListAsync()); Assert.Empty(await db.AgentWorkAttempts.ToListAsync());
        Assert.Equal(AgentWorkStatus.DeadLetter, (await db.AgentWorkItems.AsNoTracking().SingleAsync()).Status);
    }

    [MemoryPostgresFact]
    public async Task QueuedMemoryDeliverySharesBrokerReceiptBudgetWithoutEviction()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture); var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        Assert.True((await ReadHandler(fixture, db).HandleAsync(session, ReadSearch(fixture), default)).Succeeded);
        var receipt = await db.AgentMemoryReadReceipts.SingleAsync();
        for (var i = 1; i < 64; i++) db.AgentMemoryReadReceipts.Add(new AgentMemoryReadReceipt
        {
            Id = Guid.NewGuid(), OrganizationId = receipt.OrganizationId, EmployeeId = receipt.EmployeeId,
            InstallationId = receipt.InstallationId, RuntimeId = receipt.RuntimeId, WorkId = receipt.WorkId,
            Attempt = 1, GrantRevision = receipt.GrantRevision, Capability = receipt.Capability,
            EvidenceJson = receipt.EvidenceJson, AuthorityHash = receipt.AuthorityHash, ReceiptHash = i.ToString("D64")
        });
        await db.SaveChangesAsync();
        await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        var (inbox, work) = await QueueRecallAsync(fixture, db, turn);
        Assert.Null(await inbox.ClaimAsync(DeliverySession(session), default));
        Assert.Equal(MemoryRuntimeResetRequiredException.ReceiptCapacity, (await db.AgentRuntimeInstances.SingleAsync()).MemoryResetReasonCode);
        Assert.Equal(64, await db.AgentMemoryReadReceipts.CountAsync()); Assert.Empty(await db.AgentWorkAttempts.ToListAsync());
        Assert.Equal(AgentWorkStatus.Pending, (await db.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == work.Id)).Status);
    }

    [MemoryPostgresFact]
    public async Task QueuedMemoryDeliveryCannotReusePreviousRecipientContextForAnotherHuman()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture); var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        var (inbox, work) = await QueueRecallAsync(fixture, db, turn);
        Assert.NotNull(await inbox.ClaimAsync(DeliverySession(session), default));
        await db.AgentWorkItems.Where(x => x.Id == work.Id).ExecuteDeleteAsync();
        var human = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, EmployeeType = EmployeeType.Human };
        var conversation = new Conversation { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, AgentOrganizationUserId = fixture.EmployeeId, InitiatedByOrganizationUserId = human.Id };
        var message = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = conversation.Id, Role = ConversationRole.User, SenderOrganizationUserId = human.Id, Content = "A different human" };
        var nextTurn = new ChatTurn { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, ConversationId = conversation.Id,
            TargetAgentOrganizationUserId = fixture.EmployeeId, UserMessageId = message.Id, Status = ChatTurnStatus.RecallingMemory };
        db.AddRange(human, conversation, message, nextTurn); await db.SaveChangesAsync();
        var (nextInbox, nextWork) = await QueueRecallAsync(fixture, db, nextTurn, includeMemory: false);
        // The runtime still holds the first human's recalled context, so it is rotated before the second
        // human's turn is delivered; the turn waits, undelivered, for a fresh runtime.
        Assert.Null(await nextInbox.ClaimAsync(DeliverySession(session), default));
        Assert.Equal(AgentWorkStatus.Pending, (await db.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == nextWork.Id)).Status);
        Assert.Empty(await db.AgentWorkAttempts.AsNoTracking().Where(x => x.AgentWorkItemId == nextWork.Id).ToListAsync());
        Assert.Equal(MemoryRuntimeResetRequiredException.RetainedEvidence, (await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetReasonCode);
        Assert.Contains("validation=claim.queued-recall.consumer-audience",
            Assert.Single(await db.AgentRuntimeEvents.AsNoTracking().ToListAsync()).Reason);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, nextWork.Id, default));
    }
}
