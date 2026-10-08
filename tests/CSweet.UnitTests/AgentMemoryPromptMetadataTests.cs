using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private static async Task<(Guid Mention, Guid Attachment)> SeedPromptMetadataAsync(DurabilityFixture fixture, ChatTurn turn,
        CSweetDbContext db, bool attachment = true)
    {
        (await db.CoreOrganizationUsers.SingleAsync(x => x.Id == fixture.HumanId)).DisplayName = "private-sender-label";
        (await db.CoreOrganizationUsers.SingleAsync(x => x.Id == fixture.EmployeeId)).DisplayName = "private-mentioned-label";
        var mention = new ConversationMessageMention { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
            ConversationId = turn.ConversationId, MessageId = turn.UserMessageId, MentionedOrganizationUserId = fixture.EmployeeId,
            Offset = 0, Length = 4, DisplayText = "What", CreatedAt = DateTimeOffset.UtcNow };
        db.ConversationMessageMentions.Add(mention);
        var file = Guid.NewGuid();
        if (attachment)
        {
            var asset = new MediaAsset { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, FileName = "private-file-label.txt",
                ContentType = "text/plain", SizeBytes = 10, Sha256 = new string('A', 64), StorageKey = "private-storage-key", CreatedAt = DateTimeOffset.UtcNow };
            db.MediaAssets.Add(asset);
            db.ConversationMessageAttachments.Add(new() { Id = file, OrganizationId = fixture.OrganizationId, ConversationId = turn.ConversationId,
                MessageId = turn.UserMessageId, MediaAssetId = asset.Id, FileName = asset.FileName, ContentType = asset.ContentType,
                SizeBytes = asset.SizeBytes, Sha256 = asset.Sha256, CreatedAt = DateTimeOffset.UtcNow });
        }
        await db.SaveChangesAsync(); return (mention.Id, file);
    }

    [MemoryPostgresFact]
    public async Task PromptMetadataFreezesPrimaryMessageAndEventWithoutPersistingLabels()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await SeedPromptMetadataAsync(fixture, turn, db);
        var prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id, false);
        var receipt = JsonSerializer.Deserialize<MemoryRecallDispatchEvidence.Receipt>(prepared.ReceiptJson, MemoryRecallDispatchEvidence.Json)!;
        Assert.Equal(2, receipt.Prompt!.Version); Assert.NotNull(receipt.Prompt.Auxiliary);
        Assert.Contains("private-sender-label", prepared.AgentPrompt); Assert.Contains("private-mentioned-label", prepared.AgentPrompt);
        Assert.Equal("private-sender-label", prepared.Metadata!.Context["senderDisplayName"]);
        Assert.Contains("private-mentioned-label", prepared.Metadata.Context["mentionsJson"]);
        Assert.Equal("private-file-label.txt", Assert.Single(prepared.Metadata.Attachments).FileName);
        Assert.DoesNotContain("private-", prepared.ReceiptJson);
        MemoryRecallDispatchEvidence.ValidatePromptPayload(prepared.ReceiptJson, RecallPayload(prepared, turn));
        Assert.Throws<ProviderDispatchDeniedException>(() => MemoryRecallDispatchEvidence.BindRenderedPrompt(prepared.ReceiptJson,
            prepared.ConversationPrompt, "unverified header\n" + prepared.ConversationPrompt));
    }

    [MemoryPostgresTheory]
    [InlineData("sender-name")]
    [InlineData("sender-role")]
    [InlineData("mention-name")]
    [InlineData("mention-offset")]
    [InlineData("mention-text")]
    [InlineData("mention-deleted")]
    [InlineData("mention-added")]
    [InlineData("attachment-name")]
    [InlineData("attachment-deleted")]
    [InlineData("attachment-asset")]
    [InlineData("asset-storage")]
    [InlineData("asset-provenance")]
    [InlineData("asset-organization")]
    public async Task PromptMetadataRechecksEveryCurrentSourceBeforeDispatchWithEmptyRecall(string changed)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var sources = await SeedPromptMetadataAsync(fixture, turn, db);
        if (changed == "sender-role")
        {
            var role = new Role { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, Name = "private-role-label",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            db.CoreRoles.Add(role); (await db.CoreOrganizationUsers.SingleAsync(x => x.Id == fixture.HumanId)).RoleId = role.Id;
            await db.SaveChangesAsync();
        }
        var prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id, false);
        var work = ReceiptWork(fixture, turn, prepared.ReceiptJson);
        await new MemoryRecallDispatchEvidence(db).AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default);
        var mention = await db.ConversationMessageMentions.SingleAsync(x => x.Id == sources.Mention);
        var attachment = await db.ConversationMessageAttachments.SingleAsync(x => x.Id == sources.Attachment);
        var asset = await db.MediaAssets.SingleAsync(x => x.Id == attachment.MediaAssetId);
        switch (changed)
        {
            case "sender-name": (await db.CoreOrganizationUsers.SingleAsync(x => x.Id == fixture.HumanId)).DisplayName = "changed"; break;
            case "sender-role": (await db.CoreRoles.SingleAsync()).Name = "changed"; break;
            case "mention-name": (await db.CoreOrganizationUsers.SingleAsync(x => x.Id == fixture.EmployeeId)).DisplayName = "changed"; break;
            case "mention-offset": mention.Offset++; break;
            case "mention-text": mention.DisplayText = "altered"; break;
            case "mention-deleted": db.ConversationMessageMentions.Remove(mention); break;
            case "mention-added": db.ConversationMessageMentions.Add(new() { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
                ConversationId = turn.ConversationId, MessageId = turn.UserMessageId, MentionedOrganizationUserId = fixture.HumanId,
                Offset = 5, Length = 2, DisplayText = "is", CreatedAt = DateTimeOffset.UtcNow }); break;
            case "attachment-name": attachment.FileName = "changed.txt"; break;
            case "attachment-deleted": db.ConversationMessageAttachments.Remove(attachment); break;
            case "attachment-asset": attachment.Sha256 = new string('B', 64); break;
            case "asset-storage": asset.StorageKey = "changed"; break;
            case "asset-provenance": asset.ProvenanceJson = "{\"changed\":true}"; break;
            case "asset-organization":
                var foreign = new Organization { Id = Guid.NewGuid(), Name = "Foreign", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
                db.CoreOrganizations.Add(foreign); asset.OrganizationId = foreign.Id; break;
        }
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new MemoryRecallDispatchEvidence(db)
            .AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default));
        Assert.Equal("private-sender-label", prepared.Metadata!.Context["senderDisplayName"]);
    }

    [MemoryPostgresTheory]
    [InlineData("context")]
    [InlineData("attachments")]
    [InlineData("userId")]
    [InlineData("messageId")]
    [InlineData("extra")]
    public async Task PromptMetadataRejectsPayloadSubstitutionBeforeSavingWork(string changed)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await SeedPromptMetadataAsync(fixture, turn, db);
        var prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id, false);
        var payload = JsonNode.Parse(RecallPayload(prepared, turn).GetRawText())!.AsObject();
        switch (changed)
        {
            case "context": payload["context"]!["senderDisplayName"] = "substitution"; break;
            case "attachments": payload["attachments"]![0]!["fileName"] = "substitution.txt"; break;
            case "userId": payload["userId"] = fixture.EmployeeId.ToString("D"); break;
            case "messageId": payload["messageId"] = Guid.NewGuid(); break;
            case "extra": payload["unreceiptedText"] = "substitution"; break;
        }
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => inbox.EnqueueAsync(fixture.OrganizationId.ToString("D"), fixture.InstallationId,
            AgentWorkKind.Event, "user-message", JsonSerializer.SerializeToElement(payload), "metadata-substitution", DateTimeOffset.UtcNow.AddMinutes(10),
            sourceType: "chat-turn", sourceId: turn.Id.ToString("D"), memoryRecallReceiptJson: prepared.ReceiptJson));
        Assert.Empty(await db.AgentWorkItems.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task PromptMetadataRetainedDeliveryIsRecheckedAfterWorkDeletion()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture); var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        await SeedPromptMetadataAsync(fixture, turn, db, attachment: false);
        var (inbox, work) = await QueueRecallAsync(fixture, db, turn, false);
        Assert.NotNull(await inbox.ClaimAsync(DeliverySession(session), default));
        await db.AgentWorkItems.Where(x => x.Id == work.Id).ExecuteDeleteAsync();
        await db.ChatTurns.Where(x => x.Id == turn.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ChatTurnStatus.Completed));
        var next = await SeedRecallTurnAsync(fixture); var (nextInbox, nextWork) = await QueueRecallAsync(fixture, db, next, false);
        Assert.NotNull(await nextInbox.ClaimAsync(DeliverySession(session), default));
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, nextWork.Id, default);
        await db.ConversationMessageMentions.Where(x => x.MessageId == turn.UserMessageId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DisplayText, "changed"));
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, nextWork.Id, default));
    }

    [MemoryPostgresFact]
    public async Task PromptMetadataErasureWithAttachmentsRequiresIndependentRetentionReview()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, turn) = await SeedErasureInventoryAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await SeedPromptMetadataAsync(fixture, turn, db);
        await QueueRecallAsync(fixture, db, turn, false);
        var impact = await new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System)
            .GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor);
        Assert.Equal("memory_erasure_work_attachment_review_required", impact.BlockedReason); Assert.Null(impact.Execution);
        Assert.Empty(await db.MemorySourceInvalidations.ToListAsync()); Assert.Null((await db.AgentWorkItems.SingleAsync()).MemoryErasedAt);
    }

    [MemoryPostgresFact]
    public async Task PromptMetadataMigrationFencesVersionTwoRuntimesAndPreservesTheirEvidence()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await db.AgentRuntimeInstances.ExecuteUpdateAsync(s => s.SetProperty(x => x.MemoryReadEvidenceVersion, 2));
        var migration = new ChatPromptMetadataEvidence();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await RunConversationMigrationAsync(db, migration.UpOperations); await transaction.CommitAsync();
        }
        Assert.Equal(0, (await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryReadEvidenceVersion);
        Assert.Equal(work.Id, (await db.AgentWorkItems.AsNoTracking().SingleAsync()).Id);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default));
        await RunConversationMigrationAsync(db, migration.DownOperations);
        await db.AgentRuntimeInstances.ExecuteUpdateAsync(s => s.SetProperty(x => x.MemoryReadEvidenceVersion, AgentRuntimeInstance.CurrentMemoryReadEvidenceVersion));
        var error = await Assert.ThrowsAsync<PostgresException>(() => RunConversationMigrationAsync(db, migration.DownOperations));
        Assert.Contains("memory_prompt_metadata_downgrade_requires_snapshot", error.MessageText);
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptMetadataWorkContextBindsProfileAndRevision(bool changeRevision)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var stream = new Workstream { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, Name = "Test",
            ProfileKey = "private-profile-label", Revision = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.Workstreams.Add(stream); (await db.CoreConversations.SingleAsync()).WorkstreamId = stream.Id; await db.SaveChangesAsync();
        var prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id, false);
        Assert.NotNull(prepared.Metadata!.WorkContext); Assert.DoesNotContain("private-profile", prepared.ReceiptJson);
        MemoryRecallDispatchEvidence.ValidatePromptPayload(prepared.ReceiptJson, RecallPayload(prepared, turn));
        var work = ReceiptWork(fixture, turn, prepared.ReceiptJson);
        if (changeRevision) stream.Revision++; else stream.ProfileKey = "changed-profile";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new MemoryRecallDispatchEvidence(db)
            .AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default));
    }

    [MemoryPostgresFact]
    public async Task PromptMetadataRechecksReportingAncestorsBeyondTheImmediateEmployee()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var manager = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, EmployeeType = EmployeeType.Agent,
            ReportsToOrganizationUserId = fixture.HumanId, CreatedAt = DateTimeOffset.UtcNow };
        db.CoreOrganizationUsers.Add(manager);
        (await db.CoreOrganizationUsers.SingleAsync(x => x.Id == fixture.EmployeeId)).ReportsToOrganizationUserId = manager.Id;
        await db.SaveChangesAsync();
        var prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id, false);
        Assert.Equal("true", prepared.Metadata!.Context["senderIsReportingAncestor"]);
        var work = ReceiptWork(fixture, turn, prepared.ReceiptJson);
        manager.ReportsToOrganizationUserId = null; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new MemoryRecallDispatchEvidence(db)
            .AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default));
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptMetadataBoundsSourceManifestsBeforeDelivery(bool attachments)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        (await db.CoreConversationMessages.SingleAsync(x => x.Id == turn.UserMessageId)).Content = new string('x', 2000);
        for (var index = 0; index < (attachments ? 33 : 101); index++)
        {
            if (attachments)
            {
                var asset = new MediaAsset { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, FileName = "Test.txt", ContentType = "text/plain",
                    SizeBytes = 1, Sha256 = new string('A', 64), StorageKey = $"test-{index}", CreatedAt = DateTimeOffset.UtcNow };
                db.MediaAssets.Add(asset); db.ConversationMessageAttachments.Add(new() { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
                    ConversationId = turn.ConversationId, MessageId = turn.UserMessageId, MediaAssetId = asset.Id, FileName = asset.FileName,
                    ContentType = asset.ContentType, SizeBytes = asset.SizeBytes, Sha256 = asset.Sha256, CreatedAt = DateTimeOffset.UtcNow });
            }
            else db.ConversationMessageMentions.Add(new() { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, ConversationId = turn.ConversationId,
                MessageId = turn.UserMessageId, MentionedOrganizationUserId = fixture.EmployeeId, Offset = index, Length = 1,
                DisplayText = "x", CreatedAt = DateTimeOffset.UtcNow });
        }
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id, false));
        Assert.Empty(await db.AgentWorkItems.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task PromptMetadataLegacyTranscriptOnlyReceiptCannotAuthorizeDispatchOrErasure()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, turn) = await SeedErasureInventoryAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var (_, work) = await QueueRecallAsync(fixture, db, turn, false);
        var node = JsonNode.Parse(work.MemoryRecallReceiptJson!)!.AsObject(); node["prompt"]!["version"] = 1;
        node["prompt"]!.AsObject().Remove("auxiliary");
        await db.AgentWorkItems.Where(x => x.Id == work.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.MemoryRecallReceiptJson, node.ToJsonString()));
        var current = await db.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == work.Id);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new MemoryRecallDispatchEvidence(db)
            .AuthorizeWorkAsync(current, fixture.EmployeeId.ToString("D"), default));
        var impact = await new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System)
            .GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor);
        Assert.Equal("memory_erasure_work_prompt_review_required", impact.BlockedReason); Assert.Null(impact.Execution);
    }
}
