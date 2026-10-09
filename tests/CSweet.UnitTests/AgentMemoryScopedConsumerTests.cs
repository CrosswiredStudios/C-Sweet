using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopedAudienceCaseEventConsumerRequiresCurrentCanonicalClaimInsteadOfPayloadHints(bool restore)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, "Case");
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context();
        var eventId = Guid.NewGuid();
        var current = await db.AgentWorkItems.SingleAsync(x => x.Id == work.Id);
        current.Kind = AgentWorkKind.Event; current.SourceType = "platform-event";
        current.SourceId = eventId.ToString("D");
        current.ProtectedPayload = JsonSerializer.SerializeToUtf8Bytes(new { workItemId = scoped.Item, caseId = scoped.Item });
        await db.SaveChangesAsync();
        var request = ReadRequest("search", new MemorySearchRequest(scoped.Episode.Partition, scoped.Episode.Scope, "Alice"));
        var denied = await ReadHandler(fixture, db).HandleAsync(session, request, default);
        Assert.False(denied.Succeeded);
        Assert.DoesNotContain("Alice", denied.Payload.ToStringUtf8());
        Assert.Empty(await db.AgentMemoryReadReceipts.ToArrayAsync());

        // Only the platform's actual task claim binds this event to the canonical case.
        await db.CoreWorkTasks.Where(x => x.Id == scoped.Item).ExecuteUpdateAsync(x => x.SetProperty(p => p.ClaimEventId, eventId));
        var read = await ReadHandler(fixture, db).HandleAsync(session, request, default);
        Assert.True(read.Succeeded, read.Error);
        Assert.Contains("Alice", read.Payload.ToStringUtf8());
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
        var ticket = await db.CoreWorkTasks.AsNoTracking().SingleAsync(x => x.Id == scoped.Item);
        AgentTicketFeedback.RecordClaim(db, ticket, fixture.InstallationId, eventId, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        await db.CoreWorkTasks.Where(x => x.Id == scoped.Item).ExecuteUpdateAsync(x => x.SetProperty(p => p.ClaimEventId, (Guid?)null));
        if (restore) await db.CoreWorkTasks.Where(x => x.Id == scoped.Item).ExecuteUpdateAsync(x => x.SetProperty(p => p.ClaimEventId, eventId));
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() =>
            new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default));
        Assert.NotNull((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
    }

    [MemoryPostgresFact]
    public async Task ScopedAudienceConversationCannotBeReadIntoAnotherConversationWithTheSameParticipants()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, "Conversation");
        var turn = await SeedRecallTurnAsync(fixture);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context();
        var conversation = new Conversation { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
            AgentOrganizationUserId = fixture.EmployeeId, InitiatedByOrganizationUserId = fixture.HumanId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.CoreConversations.Add(conversation);
        foreach (var person in new[] { fixture.EmployeeId, fixture.HumanId })
            db.ConversationParticipants.Add(new ConversationParticipant { Id = Guid.NewGuid(), ConversationId = conversation.Id,
                OrganizationUserId = person, JoinedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        var currentTurn = await db.ChatTurns.SingleAsync(x => x.Id == turn.Id);
        currentTurn.ConversationId = conversation.Id;
        var currentWork = await db.AgentWorkItems.SingleAsync(x => x.Id == work.Id);
        currentWork.SourceType = "chat-turn"; currentWork.SourceId = turn.Id.ToString("D");
        await db.SaveChangesAsync();
        await MemoryScopedAudienceAuthorization.RequireAsync(db, fixture.OrganizationId, fixture.EmployeeId,
            fixture.HumanId, scoped.Episode.Partition, default);
        var read = await ReadHandler(fixture, db).HandleAsync(session,
            ReadRequest("search", new MemorySearchRequest(scoped.Episode.Partition, scoped.Episode.Scope, "Alice")), default);
        Assert.False(read.Succeeded);
        Assert.DoesNotContain("Alice", read.Payload.ToStringUtf8());
        Assert.Empty(await db.AgentMemoryReadReceipts.ToArrayAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("Case")]
    [InlineData("Conversation")]
    public async Task ScopedAudienceScopeIdCannotBeUsedAsAnEmployeeTransferTarget(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, kind);
        await using var db = fixture.Context();
        var transfer = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => transfer.PrepareAsync(fixture.OrganizationId,
            fixture.EmployeeId, scoped.User, new(Guid.NewGuid(), scoped.Item ?? scoped.Conversation,
                "Relationship", [], "Attempt to target a scoped audience")));
        Assert.Empty(await db.MemoryTransferReceipts.ToArrayAsync());
    }
}
