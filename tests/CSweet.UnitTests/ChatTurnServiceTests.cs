using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ChatTurnServiceTests
{
    [Fact]
    public async Task TurnLifecycle_PersistsOrderedTraceOutputAndCompletion()
    {
        await using var db = CreateDb();
        var organizationId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId,
            AgentOrganizationUserId = agentId, InitiatedByOrganizationUserId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.CoreOrganizationUsers.Add(CreateAgent(organizationId, agentId));
        db.CoreConversations.Add(conversation);
        await db.SaveChangesAsync();
        var service = new ChatTurnService(db);

        var started = await service.StartAsync(organizationId, conversation.Id, "Remember the launch date.");
        Assert.NotNull(started);
        Assert.Equal(started!.Turn.Id, started.UserMessage.ChatTurnId);
        Assert.Single(await db.MemoryCaptureOutbox.ToListAsync());

        Assert.Equal(started.Turn.Id, await service.ClaimNextAsync("test-worker"));
        var first = await service.TraceAsync(started.Turn.Id, "memory", "recall.started", "running", "Searching memory");
        var second = await service.TraceAsync(started.Turn.Id, "model", "model.dispatched", "running", "Model started");
        await service.AppendOutputAsync(started.Turn.Id, "Launch ");
        await service.AppendOutputAsync(started.Turn.Id, "Friday");
        await service.ReplaceOutputAsync(started.Turn.Id, "Validated launch Friday");

        var assistant = new ConversationMessage
        {
            Id = Guid.NewGuid(), ConversationId = conversation.Id, ChatTurnId = started.Turn.Id,
            Role = ConversationRole.Assistant, Content = "Validated launch Friday", CreatedAt = DateTimeOffset.UtcNow
        };
        db.CoreConversationMessages.Add(assistant);
        await db.SaveChangesAsync();
        await service.CompleteAsync(started.Turn.Id, assistant.Id, memoryWarning: false);

        var completed = await service.GetAsync(organizationId, started.Turn.Id);
        var trace = await service.ListEventsAsync(organizationId, started.Turn.Id);
        Assert.Equal("Completed", completed!.Status);
        Assert.Equal("Validated launch Friday", completed.PartialResponse);
        Assert.Equal([0L, 1L], trace.Select(x => x.Sequence));
        Assert.Equal(0, first.Sequence);
        Assert.Equal(1, second.Sequence);
    }

    [Fact]
    public async Task FailedTurn_CanBeRetriedWithoutMutatingOriginalMessage()
    {
        await using var db = CreateDb();
        var organizationId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId,
            AgentOrganizationUserId = agentId, InitiatedByOrganizationUserId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.CoreOrganizationUsers.Add(CreateAgent(organizationId, agentId));
        db.CoreConversations.Add(conversation);
        await db.SaveChangesAsync();
        var service = new ChatTurnService(db);
        var original = (await service.StartAsync(organizationId, conversation.Id, "Original text"))!;
        await service.SetStatusAsync(original.Turn.Id, ChatTurnStatus.Failed.ToString(), "test", "failed");

        var retry = await service.RetryAsync(organizationId, original.Turn.Id);

        Assert.NotNull(retry);
        Assert.Equal("Original text", retry!.UserMessage.Content);
        Assert.NotEqual(original.UserMessage.Id, retry.UserMessage.Id);
        Assert.Equal(original.Turn.Id, (await db.ChatTurns.SingleAsync(x => x.Id == retry.Turn.Id)).RetryOfTurnId);
    }

    [Fact]
    public async Task CompletingTurn_AttachesSubmittedArtifactRevisionCreatedByTargetAgent()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId,
            AgentOrganizationUserId = agentId, InitiatedByOrganizationUserId = Guid.NewGuid(),
            CreatedAt = now, UpdatedAt = now
        };
        db.AddRange(CreateAgent(organizationId, agentId), conversation);
        await db.SaveChangesAsync();
        var service = new ChatTurnService(db);
        var started = (await service.StartAsync(organizationId, conversation.Id, "Create the pitch."))!;
        var artifactId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var artifact = new Artifact
        {
            Id = artifactId, OrganizationId = organizationId, Type = ArtifactType.Document,
            Title = "Game pitch", Content = "# Pitch", DocumentType = "game-vision.v1",
            CreatorDisplayName = "Test agent", CreatedByOrganizationUserId = agentId,
            OriginConversationId = conversation.Id, LatestRevisionId = revisionId,
            SubmittedRevisionId = revisionId, DocumentStatus = ArtifactDocumentStatus.InReview,
            CreatedAt = now, UpdatedAt = now
        };
        var revision = new ArtifactRevision
        {
            Id = revisionId, OrganizationId = organizationId, ArtifactId = artifactId,
            Artifact = artifact, Number = 1, Content = "# Pitch", ContentSha256 = new string('a', 64),
            Status = ArtifactRevisionStatus.Submitted, CreatorDisplayName = "Test agent",
            CreatedByOrganizationUserId = agentId, CreatedAt = started.Turn.CreatedAt.AddMilliseconds(1),
            SubmittedAt = started.Turn.CreatedAt.AddMilliseconds(2), IdempotencyKey = "pitch-revision"
        };
        artifact.Revisions.Add(revision);
        var assistant = new ConversationMessage
        {
            Id = Guid.NewGuid(), ConversationId = conversation.Id, ChatTurnId = started.Turn.Id,
            SenderOrganizationUserId = agentId, Role = ConversationRole.Assistant,
            Content = "I created the first draft.", CreatedAt = now
        };
        db.AddRange(artifact, assistant);
        await db.SaveChangesAsync();

        await service.CompleteAsync(started.Turn.Id, assistant.Id, memoryWarning: false);

        var link = await db.ConversationMessageArtifacts.SingleAsync();
        Assert.Equal(assistant.Id, link.MessageId);
        Assert.Equal(artifactId, link.ArtifactId);
        Assert.Equal(revisionId, link.RevisionId);
    }

    [Fact]
    public async Task AttachmentOnlyTurn_PersistsSanitizedDescriptorAndRetryPreservesReference()
    {
        await using var db = CreateDb();
        var organizationId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId,
            AgentOrganizationUserId = agentId, InitiatedByOrganizationUserId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        var asset = CreateAsset(organizationId, "concept.webp", "image/webp", 1024);
        db.AddRange(CreateAgent(organizationId, agentId), conversation, asset);
        await db.SaveChangesAsync();
        var service = new ChatTurnService(db);

        var original = await service.StartForAgentAsync(
            organizationId, conversation.Id, agentId, string.Empty,
            attachmentMediaAssetIds: [asset.Id]);

        Assert.NotNull(original);
        Assert.Equal("concept.webp", conversation.Title);
        var persisted = await db.CoreConversationMessages.Include(x => x.Attachments)
            .SingleAsync(x => x.Id == original!.UserMessage.Id);
        var attachment = Assert.Single(persisted.Attachments);
        Assert.Equal(asset.Id, attachment.MediaAssetId);
        Assert.Equal(asset.Sha256, attachment.Sha256);
        Assert.DoesNotContain(asset.StorageKey, original.UserMessage.Content, StringComparison.Ordinal);

        await service.SetStatusAsync(original.Turn.Id, ChatTurnStatus.Failed.ToString(), "test", "failed");
        var retry = await service.RetryAsync(organizationId, original.Turn.Id);
        Assert.NotNull(retry);
        var retryMessage = await db.CoreConversationMessages.Include(x => x.Attachments)
            .SingleAsync(x => x.Id == retry!.UserMessage.Id);
        Assert.Equal(asset.Id, Assert.Single(retryMessage.Attachments).MediaAssetId);
        Assert.Equal(attachment.Sha256, Assert.Single(retry.UserMessage.Attachments).Sha256);
    }

    [Fact]
    public async Task AttachmentValidation_DeniesForeignOversizedAndUnsupportedAssets()
    {
        await using var db = CreateDb();
        var organizationId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId,
            AgentOrganizationUserId = agentId, InitiatedByOrganizationUserId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        var foreign = CreateAsset(Guid.NewGuid(), "foreign.png", "image/png", 10);
        var oversized = CreateAsset(organizationId, "large.png", "image/png", 25L * 1024 * 1024 + 1);
        var unsupported = CreateAsset(organizationId, "archive.zip", "application/zip", 10);
        db.AddRange(CreateAgent(organizationId, agentId), conversation, foreign, oversized, unsupported);
        await db.SaveChangesAsync();
        var service = new ChatTurnService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartForAgentAsync(
            organizationId, conversation.Id, agentId, string.Empty, attachmentMediaAssetIds: [foreign.Id]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartForAgentAsync(
            organizationId, conversation.Id, agentId, string.Empty, attachmentMediaAssetIds: [oversized.Id]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartForAgentAsync(
            organizationId, conversation.Id, agentId, string.Empty, attachmentMediaAssetIds: [unsupported.Id]));
    }

    [Fact]
    public async Task CancelledTurn_CannotBeReactivatedByLateWorkerUpdates()
    {
        await using var db = CreateDb();
        var organizationId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId,
            AgentOrganizationUserId = agentId, InitiatedByOrganizationUserId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.CoreOrganizationUsers.Add(CreateAgent(organizationId, agentId));
        db.CoreConversations.Add(conversation);
        await db.SaveChangesAsync();
        var service = new ChatTurnService(db);
        var started = (await service.StartAsync(organizationId, conversation.Id, "Stop this"))!;
        Assert.Equal(started.Turn.Id, await service.ClaimNextAsync("test-worker"));
        Assert.True(await service.CancelAsync(organizationId, started.Turn.Id));

        await service.SetStatusAsync(started.Turn.Id, ChatTurnStatus.Running.ToString());
        await service.AppendOutputAsync(started.Turn.Id, "late output");
        await service.CompleteAsync(started.Turn.Id, Guid.NewGuid(), memoryWarning: false);

        var cancelled = await service.GetAsync(organizationId, started.Turn.Id);
        Assert.Equal("Cancelled", cancelled!.Status);
        Assert.Empty(cancelled.PartialResponse);
        Assert.Null(cancelled.AssistantMessageId);
    }

    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static OrganizationUser CreateAgent(Guid organizationId, Guid agentId) => new()
    {
        Id = agentId,
        OrganizationId = organizationId,
        DisplayName = "Test agent",
        EmployeeType = EmployeeType.Agent,
        PermissionLevel = OrganizationPermissionLevel.Viewer,
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static MediaAsset CreateAsset(Guid organizationId, string fileName, string contentType, long size) => new()
    {
        Id = Guid.NewGuid(), OrganizationId = organizationId, FileName = fileName,
        ContentType = contentType, SizeBytes = size, Sha256 = new string('a', 64),
        StorageKey = $"private/{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow
    };
}
