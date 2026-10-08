using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private static async Task<(Guid Actor, ChatTurn Turn)> SeedErasureInventoryAsync(DurabilityFixture fixture)
    {
        var (actor, _) = await SeedRecoveryAsync(fixture);
        await using var db = fixture.Context();
        // SeedRecovery's opaque output is useful for retry preservation tests; these
        // inventory cases start with no accepted output or unverified paired input.
        await db.MemoryCaptureOutbox.ExecuteUpdateAsync(s => s.SetProperty(x => x.AcceptedExtractionJson, (string?)null)
            .SetProperty(x => x.ExtractionAcceptedAt, (DateTimeOffset?)null));
        return (actor, await SeedRecallTurnAsync(fixture));
    }

    [MemoryPostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ErasureInventoryFindsQueuedRecallAndDirectConversationPayloadsWithoutWriting(bool includeMemory)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, turn) = await SeedErasureInventoryAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var (_, item) = await QueueRecallAsync(fixture, db, turn, includeMemory);
        var before = JsonSerializer.Serialize(await db.AgentWorkItems.AsNoTracking().SingleAsync());
        var audits = await db.AuditOutbox.CountAsync();
        var impact = await new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System)
            .GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor);
        Assert.Null(impact.BlockedReason); Assert.NotNull(impact.Execution);
        Assert.Equal(1, impact.Execution.CaptureSources); Assert.Equal(1, impact.Execution.WorkItems);
        Assert.Equal(1, impact.Execution.ExtractionJobs);
        Assert.Equal(0, impact.Execution.Runtimes); Assert.Equal(0, impact.Execution.ActiveRuntimes);
        Assert.Equal(before, JsonSerializer.Serialize(await db.AgentWorkItems.AsNoTracking().SingleAsync()));
        Assert.Equal(audits, await db.AuditOutbox.CountAsync());
        Assert.DoesNotContain(item.Id.ToString(), JsonSerializer.Serialize(impact));
        Assert.DoesNotContain("Alice", JsonSerializer.Serialize(impact));
    }

    [MemoryPostgresFact]
    public async Task ErasureInventoryFindsDeliveredContextAfterOriginalWorkWasDeleted()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, turn) = await SeedErasureInventoryAsync(fixture);
        var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        var (inbox, work) = await QueueRecallAsync(fixture, db, turn);
        Assert.NotNull(await inbox.ClaimAsync(DeliverySession(session), default));
        await db.AgentWorkItems.Where(x => x.Id == work.Id).ExecuteDeleteAsync(); db.ChangeTracker.Clear();
        var (laterInbox, later) = await QueueRecallAsync(fixture, db, turn, includeMemory: false);
        Assert.NotNull(await laterInbox.ClaimAsync(DeliverySession(session), default));
        var impact = await new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System)
            .GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor);
        Assert.Null(impact.BlockedReason); Assert.Equal(1, impact.Execution!.WorkItems);
        Assert.Equal(1, impact.Execution.Runtimes); Assert.Equal(1, impact.Execution.ActiveRuntimes);
        Assert.Null((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
        Assert.Null((await db.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == later.Id)).MemoryErasedAt);
    }

    [MemoryPostgresFact]
    public async Task ErasureInventoryRefusesAnotherHumansPromptOnTheSameRetainedRuntime()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, turn) = await SeedErasureInventoryAsync(fixture);
        var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        var (inbox, work) = await QueueRecallAsync(fixture, db, turn);
        Assert.NotNull(await inbox.ClaimAsync(DeliverySession(session), default));
        var nextReceipt = JsonSerializer.Deserialize<MemoryRecallDispatchEvidence.Receipt>(work.MemoryRecallReceiptJson!, MemoryRecallDispatchEvidence.Json)!;
        var otherHuman = Guid.NewGuid();
        nextReceipt = nextReceipt with { Binding = nextReceipt.Binding with { HumanId = otherHuman },
            Prompt = nextReceipt.Prompt! with { Auxiliary = nextReceipt.Prompt.Auxiliary! with { SenderId = otherHuman } } };
        var other = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId.ToString("D"), AgentInstallationId = fixture.InstallationId,
            SourceType = "chat-turn", SourceId = turn.Id.ToString("D"), PayloadHash = work.PayloadHash, IdempotencyKey = "other-human",
            MemoryRecallReceiptJson = MemoryRecallDispatchEvidence.Serialize(nextReceipt) };
        db.AgentWorkItems.Add(other);
        db.AgentWorkAttempts.Add(new() { Id = Guid.NewGuid(), AgentWorkItemId = other.Id, RuntimeInstanceId = Guid.Parse(session.RuntimeInstanceId), Attempt = 1 });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System)
            .GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor));
    }

    [MemoryPostgresTheory]
    [InlineData("legacy-chat")]
    [InlineData("malformed")]
    [InlineData("unknown-field")]
    [InlineData("duplicate-field")]
    [InlineData("bad-payload-binding")]
    public async Task ErasureInventoryFailsClosedForIncompleteOrAmbiguousWorkReceipts(string corruption)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, turn) = await SeedErasureInventoryAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var (_, work) = await QueueRecallAsync(fixture, db, turn);
        var node = JsonNode.Parse(work.MemoryRecallReceiptJson!)!.AsObject();
        if (corruption == "unknown-field") node["unknownContent"] = "private";
        if (corruption == "bad-payload-binding") node["payloadHash"] = new string('a', 64);
        string? json = corruption switch { "legacy-chat" => null, "malformed" => "{}", "duplicate-field" =>
            work.MemoryRecallReceiptJson![..^1] + ",\"Version\":1}", _ => node.ToJsonString() };
        await db.AgentWorkItems.Where(x => x.Id == work.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.MemoryRecallReceiptJson, json));
        var impact = await new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System)
            .GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor);
        Assert.Equal("memory_erasure_work_lineage_review_required", impact.BlockedReason); Assert.Null(impact.Execution);
        Assert.Null((await db.AgentWorkItems.AsNoTracking().SingleAsync()).MemoryErasedAt);
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ErasureInventoryRetainsIndependentHeldSourcesInsideSelectedWork(bool missing)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, turn) = await SeedErasureInventoryAsync(fixture);
        var held = new MemoryEpisode(Guid.NewGuid(), fixture.Partition, MemoryScope.User, "Alice also prefers morning meetings", "text/plain",
            new("fixture", "independent-held"), "hash", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, LegalHold: true);
        await fixture.Store.AppendEpisodeAsync(held);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var (_, work) = await QueueRecallAsync(fixture, db, turn);
        var receipt = JsonSerializer.Deserialize<MemoryRecallDispatchEvidence.Receipt>(work.MemoryRecallReceiptJson!, MemoryRecallDispatchEvidence.Json)!;
        // Retained historical work can include another source beyond today's selected
        // recall candidates. Its independent hold must protect that work too.
        var historicalReceipt = MemoryRecallDispatchEvidence.Serialize(receipt with
        { Records = [.. receipt.Records, receipt.Records[0] with { Id = held.Id }] });
        await db.AgentWorkItems.Where(x => x.Id == work.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.MemoryRecallReceiptJson, historicalReceipt));
        if (missing) await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM csweet_memory_episodes WHERE id={held.Id}");
        var impact = await new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System)
            .GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor);
        if (missing)
        {
            Assert.Equal("memory_erasure_work_retention_review_required", impact.BlockedReason);
            Assert.Null(impact.Execution); return;
        }
        Assert.Equal("memory_legal_hold_prevents_deletion", impact.BlockedReason);
        Assert.Equal(1, impact.Execution!.WorkItems);
        Assert.Equal("memory_legal_hold_prevents_deletion", impact.Execution.BlockedReason);
    }

    [MemoryPostgresFact]
    public async Task ErasureInventoryFindsBrokerReadAfterItsWorkWasDeleted()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedErasureInventoryAsync(fixture);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var result = await ReadHandler(fixture, db).HandleAsync(session, ReadSearch(fixture), default);
        Assert.True(result.Succeeded, result.Error);
        Assert.Single(await db.AgentMemoryReadReceipts.ToListAsync());
        await db.AgentWorkItems.Where(x => x.Id == work.Id).ExecuteDeleteAsync(); db.ChangeTracker.Clear();
        var impact = await new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System)
            .GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor);
        Assert.Null(impact.BlockedReason); Assert.Equal(0, impact.Execution!.WorkItems);
        Assert.Equal(1, impact.Execution.Runtimes); Assert.Equal(1, impact.Execution.ActiveRuntimes);
        Assert.Null((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
    }

    [MemoryPostgresFact]
    public async Task ErasureInventoryRequiresReviewForLegacyRuntimeWithoutReadEvidence()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedErasureInventoryAsync(fixture);
        await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await db.AgentRuntimeInstances.ExecuteUpdateAsync(s => s.SetProperty(x => x.MemoryReadEvidenceVersion, 0));
        var impact = await new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System)
            .GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor);
        Assert.Equal("memory_erasure_work_lineage_review_required", impact.BlockedReason); Assert.Null(impact.Execution);
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ErasureInventoryUsesVerifiedHistoricalSourceAfterConversationDeletion(bool currentRelationship)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, turn) = await SeedErasureInventoryAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await QueueRecallAsync(fixture, db, turn, includeMemory: false);
        await db.CoreConversations.Where(x => x.Id == turn.ConversationId).ExecuteDeleteAsync(); db.ChangeTracker.Clear();
        if (!currentRelationship)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System)
                .GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor));
            return;
        }
        db.CoreConversations.Add(new Conversation { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
            AgentOrganizationUserId = fixture.EmployeeId, InitiatedByOrganizationUserId = fixture.HumanId });
        await db.SaveChangesAsync();
        var impact = await new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System)
            .GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor);
        Assert.Null(impact.BlockedReason); Assert.Equal(1, impact.Execution!.CaptureSources);
        Assert.Equal(1, impact.Execution.WorkItems); Assert.Equal(0, impact.Execution.ExtractionJobs);
        Assert.Empty(await db.MemorySourceInvalidations.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task ErasureInventoryRequiresReviewForNonChatWorkExpandedFromARuntime()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, turn) = await SeedErasureInventoryAsync(fixture);
        var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var (_, work) = await QueueRecallAsync(fixture, db, turn);
        db.AgentWorkAttempts.Add(new() { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, RuntimeInstanceId = Guid.Parse(session.RuntimeInstanceId), Attempt = 1 });
        await db.SaveChangesAsync();
        var impact = await new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System)
            .GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor);
        Assert.Equal("memory_erasure_work_audience_review_required", impact.BlockedReason); Assert.Null(impact.Execution);
        Assert.Null((await db.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == initial.Id)).MemoryErasedAt);
    }
}
