using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.AgentHost.Broker;
using CSweet.Application.Core;
using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresFact]
    public async Task PromptInputsFreezeExactlyTheBoundedRenderedHistoryWithoutSavingText()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var conversation = await db.CoreConversationMessages.Select(x => x.ConversationId).SingleAsync();
        for (var i = 1; i <= 25; i++)
        {
            db.CoreConversationMessages.Add(new() { Id = Guid.NewGuid(), ConversationId = conversation,
                Role = ConversationRole.Assistant, Content = $"history-{i:D2}:" + new string('x', 5000), CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(); // Sequence is database generated; persist chronological fixture order.
        }
        db.CoreConversationMessages.Add(new() { Id = Guid.NewGuid(), ConversationId = conversation, Role = ConversationRole.Assistant,
            Content = "system-action-private-text", SourceProvider = CommunicationMessageTypes.SystemAction, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var turn = await SeedRecallTurnAsync(fixture);
        var prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id, includeMemory: false);
        var receipt = JsonSerializer.Deserialize<MemoryRecallDispatchEvidence.Receipt>(prepared.ReceiptJson, MemoryRecallDispatchEvidence.Json)!;
        Assert.Equal(2, receipt.Version); Assert.NotNull(receipt.Prompt); Assert.Equal(4, receipt.Prompt.Inputs.Length);
        Assert.Equal(ConversationPromptRenderer.RecentCharacterBudget, receipt.Prompt.Inputs[..^1].Sum(x => x.RenderedLength));
        Assert.All(receipt.Prompt.Inputs[..^1], x => Assert.Equal(4000, x.RenderedLength));
        Assert.Equal(turn.UserMessageId, receipt.Prompt.Inputs[^1].Id);
        Assert.Contains("history-25", prepared.ConversationPrompt); Assert.DoesNotContain("history-22", prepared.ConversationPrompt);
        Assert.DoesNotContain("system-action-private-text", prepared.ConversationPrompt); Assert.DoesNotContain("history-", prepared.ReceiptJson);
        Assert.DoesNotContain("Alice", prepared.ReceiptJson); Assert.Equal("What is my name?", prepared.CurrentMessageContent);
        Assert.Equal(MemoryRecallDispatchEvidence.Hash(prepared.ConversationPrompt), receipt.Prompt.ConversationHash);
    }

    [MemoryPostgresTheory]
    [InlineData("content")]
    [InlineData("sender")]
    [InlineData("sequence")]
    [InlineData("timestamp")]
    [InlineData("exclusion")]
    [InlineData("invalidation")]
    [InlineData("deleted")]
    public async Task PromptInputsRejectChangedRecentMessageEvenWhenNoMemoryWasSelected(string changed)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id, includeMemory: false);
        var work = ReceiptWork(fixture, turn, prepared.ReceiptJson);
        await new MemoryRecallDispatchEvidence(db).AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default);
        var source = await db.CoreConversationMessages.SingleAsync(x => x.Id == fixture.MessageId);
        switch (changed)
        {
            case "content": source.Content = "Changed private history"; break;
            case "sender": source.SenderOrganizationUserId = fixture.EmployeeId; break;
            case "sequence": source.Sequence = 1000; break;
            case "timestamp": source.CreatedAt = source.CreatedAt.AddSeconds(1); break;
            case "deleted": db.CoreConversationMessages.Remove(source); break;
            case "invalidation": db.MemorySourceInvalidations.Add(new() { SourceMessageId = source.Id,
                PreviousConversationId = source.ConversationId, InvalidatedAt = DateTimeOffset.UtcNow, ReasonCode = "memory_source_invalidated" }); break;
            case "exclusion": db.MemoryCaptureExclusions.Add(new() { SourceMessageId = source.Id, OrganizationId = fixture.OrganizationId,
                EmployeeId = fixture.EmployeeId, TriggerJobId = Guid.NewGuid(), ExcludedAt = DateTimeOffset.UtcNow, ReasonCode = "memory_source_invalidated" }); break;
        }
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new MemoryRecallDispatchEvidence(db)
            .AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default));
        if (changed is "deleted" or "invalidation" or "exclusion")
        {
            var fresh = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id, includeMemory: false);
            var freshReceipt = JsonSerializer.Deserialize<MemoryRecallDispatchEvidence.Receipt>(fresh.ReceiptJson, MemoryRecallDispatchEvidence.Json)!;
            Assert.DoesNotContain(freshReceipt.Prompt!.Inputs, x => x.Id == fixture.MessageId);
            Assert.Equal(turn.UserMessageId, freshReceipt.Prompt.Inputs[^1].Id);
            Assert.Equal("What is my name?", fresh.CurrentMessageContent);
        }
    }

    [MemoryPostgresFact]
    public async Task PromptInputsBindRenderedAgentMessageBeforeEnqueue()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id, includeMemory: false);
        var full = prepared.AgentPrompt;
        var receipt = MemoryRecallDispatchEvidence.BindRenderedPrompt(prepared.ReceiptJson, prepared.ConversationPrompt, full);
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => inbox.EnqueueAsync(fixture.OrganizationId.ToString("D"), fixture.InstallationId,
            AgentWorkKind.Event, "user-message", JsonSerializer.SerializeToElement(new { message = "unreviewed private text" }),
            "prompt-binding", DateTimeOffset.UtcNow.AddMinutes(10), sourceType: "chat-turn", sourceId: turn.Id.ToString("D"), memoryRecallReceiptJson: receipt));
        Assert.Empty(await db.AgentWorkItems.ToListAsync());
        var work = await inbox.EnqueueAsync(fixture.OrganizationId.ToString("D"), fixture.InstallationId,
            AgentWorkKind.Event, "user-message", RecallPayload(prepared, turn),
            "prompt-binding", DateTimeOffset.UtcNow.AddMinutes(10), sourceType: "chat-turn", sourceId: turn.Id.ToString("D"), memoryRecallReceiptJson: receipt);
        Assert.NotNull(work.MemoryRecallReceiptJson);
        Assert.Throws<ProviderDispatchDeniedException>(() => MemoryRecallDispatchEvidence.BindRenderedPrompt(prepared.ReceiptJson,
            prepared.ConversationPrompt + " altered", full));
    }

    [MemoryPostgresFact]
    public async Task PromptInputsRetainEmptyRecallDeliveryAfterWorkDeletion()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture); var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        var (inbox, work) = await QueueRecallAsync(fixture, db, turn, includeMemory: false);
        Assert.NotNull(await inbox.ClaimAsync(DeliverySession(session), default));
        var receipt = await db.AgentMemoryReadReceipts.AsNoTracking().SingleAsync();
        var read = MemoryRecallDispatchEvidence.InspectErasureRead(receipt.EvidenceJson, queued: true);
        Assert.Empty(read.References); Assert.NotNull(read.Prompt); Assert.Contains(read.Prompt.Inputs, x => x.Id == fixture.MessageId);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, null, default));
        await db.AgentWorkItems.Where(x => x.Id == work.Id).ExecuteDeleteAsync();
        Assert.Single(await db.AgentMemoryReadReceipts.ToListAsync());
        // An idle context has no transient memory to supply. This helper is not
        // an execution grant, and the old work remains unavailable for dispatch.
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, null, default);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default, 1));
        Assert.Null((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
        Assert.Equal(receipt.EvidenceJson, (await db.AgentMemoryReadReceipts.AsNoTracking().SingleAsync()).EvidenceJson);
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptInputsProtectIndependentCurrentHoldOrRejectMissingRetention(bool missing)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, turn) = await SeedErasureInventoryAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await QueueRecallAsync(fixture, db, turn, includeMemory: false);
        var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        if (missing)
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM csweet_memory_episodes WHERE id={turn.UserMessageId}");
        else
        {
            var hold = await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, turn.UserMessageId, actor);
            await service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, turn.UserMessageId, actor,
                new(Guid.NewGuid(), hold.Revision, hold.EvidenceToken, true));
        }
        var impact = await service.GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor);
        Assert.Equal(missing ? "memory_erasure_work_retention_review_required" : "memory_legal_hold_prevents_deletion", impact.BlockedReason);
        if (missing) Assert.Null(impact.Execution); else Assert.NotNull(impact.Execution);
        Assert.Empty(await db.MemorySourceInvalidations.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task PromptInputsKeepLegacyQueuedEvidenceInReviewWithoutGuessingItsHistory()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, turn) = await SeedErasureInventoryAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var (_, work) = await QueueRecallAsync(fixture, db, turn, includeMemory: false);
        var node = JsonNode.Parse(work.MemoryRecallReceiptJson!)!.AsObject(); node["version"] = 1; node.Remove("prompt");
        await db.AgentWorkItems.Where(x => x.Id == work.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.MemoryRecallReceiptJson, node.ToJsonString()));
        var impact = await new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System)
            .GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor);
        Assert.Equal("memory_erasure_work_prompt_review_required", impact.BlockedReason); Assert.Null(impact.Execution);
    }

    [MemoryPostgresFact]
    public async Task PromptInputMigrationFencesPriorRuntimesAndRefusesEvidenceDowngrade()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await db.AgentRuntimeInstances.ExecuteUpdateAsync(s => s.SetProperty(x => x.MemoryReadEvidenceVersion, 1));
        var migration = new ChatPromptInputEvidence();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await RunConversationMigrationAsync(db, migration.UpOperations);
            await transaction.CommitAsync();
        }
        Assert.Equal(0, (await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryReadEvidenceVersion);
        Assert.Equal(initial.Id, (await db.AgentWorkItems.AsNoTracking().SingleAsync()).Id);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, initial.Id, default));
        await RunConversationMigrationAsync(db, migration.DownOperations);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await RunConversationMigrationAsync(db, migration.UpOperations);
            await transaction.CommitAsync();
        }
        await db.AgentRuntimeInstances.ExecuteUpdateAsync(s => s.SetProperty(x => x.MemoryReadEvidenceVersion, AgentRuntimeInstance.CurrentMemoryReadEvidenceVersion));
        var error = await Assert.ThrowsAsync<PostgresException>(() => RunConversationMigrationAsync(db, migration.DownOperations));
        Assert.Contains("memory_prompt_evidence_downgrade_requires_snapshot", error.MessageText);
    }

    [MemoryPostgresTheory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("null")]
    public async Task PromptInputsRejectMalformedOrIncompleteEvidence(string malformed)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id, includeMemory: false);
        var node = JsonNode.Parse(prepared.ReceiptJson)!.AsObject();
        var input = node["prompt"]!["inputs"]![0]!.AsObject();
        switch (malformed)
        {
            case "missing": input.Remove("sourceHash"); break;
            case "unknown": input["unexpected"] = "unreviewed"; break;
            case "null": node["binding"] = null; break;
        }
        var json = node.ToJsonString();
        if (malformed == "duplicate") json = json.Insert(1, "\"version\":2,");
        var work = ReceiptWork(fixture, turn, prepared.ReceiptJson); work.MemoryRecallReceiptJson = json;
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new MemoryRecallDispatchEvidence(db)
            .AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default));
        Assert.Throws<ProviderDispatchDeniedException>(() => MemoryRecallDispatchEvidence.BindRenderedPrompt(json,
            prepared.ConversationPrompt, prepared.ConversationPrompt));
    }

    [MemoryPostgresFact]
    public async Task PromptInputsDatabaseGuardsPreserveEvidenceThroughErasureAndRuntimeCascade()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture); var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync(ChatPromptInputEvidence.InstallTriggers);
        await db.Database.ExecuteSqlRawAsync(WorkMemoryErasure.InstallTriggers);
        await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        var (inbox, work) = await QueueRecallAsync(fixture, db, turn, includeMemory: false);
        Assert.NotNull(await inbox.ClaimAsync(DeliverySession(session), default));
        var delivered = await db.AgentMemoryReadReceipts.AsNoTracking().SingleAsync();
        foreach (var sql in new[] {
                     "UPDATE \"AgentWorkItems\" SET \"MemoryRecallReceiptJson\"=NULL WHERE \"Id\"={0}",
                     "UPDATE \"AgentWorkItems\" SET \"PayloadHash\"='changed' WHERE \"Id\"={0}",
                     "UPDATE \"AgentWorkItems\" SET \"ProtectedPayload\"=decode('00','hex') WHERE \"Id\"={0}",
                     "UPDATE \"AgentWorkItems\" SET \"SourceId\"=NULL WHERE \"Id\"={0}",
                     "UPDATE \"AgentWorkItems\" SET \"OrganizationId\"='foreign' WHERE \"Id\"={0}" })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql, work.Id));
            Assert.Contains("memory_prompt_evidence_immutable", error.MessageText);
        }
        var rewrite = await Assert.ThrowsAsync<PostgresException>(() => db.AgentMemoryReadReceipts
            .Where(x => x.Id == delivered.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.EvidenceJson, "{}")));
        Assert.Contains("memory_prompt_evidence_immutable", rewrite.MessageText);
        var delete = await Assert.ThrowsAsync<PostgresException>(() => db.AgentMemoryReadReceipts.ExecuteDeleteAsync());
        Assert.Contains("memory_prompt_evidence_immutable", delete.MessageText);
        await using (var cleanup = fixture.Context())
        await using (var transaction = await cleanup.Database.BeginTransactionAsync())
        using (var erasure = new MemoryWorkErasure(cleanup, new EphemeralDataProtectionProvider(), TimeProvider.System))
        {
            await erasure.AcquireAsync(default);
            var plan = await erasure.PrepareAsync(fixture.OrganizationId, [work.Id], [], default);
            Assert.Equal(1, (await erasure.StageAsync(plan, default)).ClearedWorks);
            await transaction.CommitAsync();
        }
        var erased = await db.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == work.Id);
        Assert.NotNull(erased.MemoryErasedAt); Assert.Equal(work.MemoryRecallReceiptJson, erased.MemoryRecallReceiptJson);
        Assert.Equal(work.PayloadHash, erased.PayloadHash); Assert.Single(await db.AgentMemoryReadReceipts.ToListAsync());
        var downgrade = await Assert.ThrowsAsync<PostgresException>(() => RunConversationMigrationAsync(db, new ChatPromptInputEvidence().DownOperations));
        Assert.Contains("memory_prompt_evidence_downgrade_requires_snapshot", downgrade.MessageText);
        await db.AgentWorkItems.Where(x => x.Id == work.Id).ExecuteDeleteAsync();
        await db.AgentRuntimeInstances.ExecuteDeleteAsync();
        Assert.Empty(await db.AgentMemoryReadReceipts.ToListAsync());
    }
}
