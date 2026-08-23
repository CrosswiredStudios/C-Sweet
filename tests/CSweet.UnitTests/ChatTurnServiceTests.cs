using CSweet.Domain.Core;
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
}
