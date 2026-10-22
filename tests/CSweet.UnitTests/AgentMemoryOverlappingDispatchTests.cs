using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingDispatchChatValidatesItsOwnReceiptWhileBackgroundWorkIsLeased(bool includeMemory)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture);
        var (session, background) = await SeedBrokerReadLeaseAsync(fixture);
        if (includeMemory) await SeedReviewClaim(fixture);
        await using var db = fixture.Context();
        // Real pickup order from Naomi's incident: background work without reads,
        // then a chat delivery (which records a receipt even with no recall hits).
        var (inbox, chat) = await QueueRecallAsync(fixture, db, turn, includeMemory);
        var claim = Assert.IsType<ClaimedAgentWork>(await inbox.ClaimAsync(DeliverySession(session), default));
        Assert.Equal(chat.Id, claim.WorkId);
        Assert.Equal(2, await db.AgentWorkAttempts.CountAsync(x => x.FinishedAt == null));
        await using var reconnect = fixture.Context();
        var evidence = new PlatformMemoryReadEvidence(reconnect);
        await evidence.AuthorizeDispatchAsync(session, chat.Id, default, claim.Attempt);
        await evidence.AuthorizeDispatchAsync(session, background.Id, default, 1);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => evidence.AuthorizeDispatchAsync(session, chat.Id, default, claim.Attempt + 1));
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => evidence.AuthorizeDispatchAsync(session, null, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => evidence.BeginAsync(session, CSweet.Agent.SDK.PlatformCapabilities.LlmChatStream, default));
        using (new InferenceExecutionAttribution(chat.Id, Guid.NewGuid(), claim.Attempt).Enter())
        {
            var instruction = await evidence.BeginAsync(session, CSweet.Agent.SDK.PlatformCapabilities.LlmChatStream, default);
            Assert.Equal(chat.Id, instruction.WorkId);
            Assert.Equal(claim.Attempt, instruction.Attempt);
            await evidence.RecordAsync(session, CSweet.Agent.SDK.PlatformCapabilities.LlmChatStream, instruction,
                Array.Empty<CSweet.Memory.MemoryEpisode>(), null, default);
        }
        using (new InferenceExecutionAttribution(chat.Id, Guid.NewGuid(), claim.Attempt + 1).Enter())
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => evidence.BeginAsync(session, CSweet.Agent.SDK.PlatformCapabilities.LlmChatStream, default));
        using (new InferenceExecutionAttribution(Guid.NewGuid(), Guid.NewGuid(), claim.Attempt).Enter())
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => evidence.BeginAsync(session, CSweet.Agent.SDK.PlatformCapabilities.LlmChatStream, default));
        Assert.Null((await reconnect.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
        Assert.Empty(await reconnect.AgentRuntimeEvents.ToListAsync());
        Assert.Single(await reconnect.AgentMemoryReadReceipts.ToListAsync());

        // Overlap must not hide genuine invalidation of the delivered chat input.
        await db.CoreConversationMessages.Where(x => x.Id == turn.UserMessageId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Content, "Changed after delivery"));
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => evidence.AuthorizeDispatchAsync(session, chat.Id, default, claim.Attempt));
        Assert.Equal(MemoryRuntimeResetRequiredException.RetainedEvidence,
            (await reconnect.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetReasonCode);
    }

    [MemoryPostgresFact]
    public async Task OverlappingDispatchExplicitReadKeepsSourceValidation()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture);
        var claim = await SeedReviewClaim(fixture);
        await fixture.Store.SetClaimConfirmationAsync(claim.Id, CSweet.Memory.MemoryConfirmationState.Confirmed);
        await using var db = fixture.Context();
        Assert.True((await ReadHandler(fixture, db).HandleAsync(session, ReadSearch(fixture), default)).Succeeded);
        // Model dispatch can overlap a callback admitted before the read was recorded.
        var other = await QueueOwnTaskNotification(new CSweet.Infrastructure.Setup.AgentWorkInbox(db,
            new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), TimeProvider.System), fixture);
        await db.AgentWorkItems.Where(x => x.Id == other.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, AgentWorkStatus.Leased).SetProperty(x => x.AttemptCount, 1));
        db.AgentWorkAttempts.Add(new() { Id = Guid.NewGuid(), AgentWorkItemId = other.Id,
            RuntimeInstanceId = Guid.Parse(session.RuntimeInstanceId), Attempt = 1,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) });
        await db.SaveChangesAsync();
        await using var reconnect = fixture.Context();
        var evidence = new PlatformMemoryReadEvidence(reconnect);
        await evidence.AuthorizeDispatchAsync(session, work.Id, default, 1);
        Assert.Null((await reconnect.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
        await fixture.Store.SetClaimConfirmationAsync(claim.Id, CSweet.Memory.MemoryConfirmationState.Rejected);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => evidence.AuthorizeDispatchAsync(session, work.Id, default, 1));
        Assert.Equal(MemoryRuntimeResetRequiredException.RetainedEvidence,
            (await reconnect.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetReasonCode);
    }
}
