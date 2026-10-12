using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Application.Setup;
using CSweet.Domain.Analytics;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class EmployeeAuditTimelineTests
{
    [Fact]
    public async Task SentMessageLinksToConversationAsSendingAgentAndRetainsLinkAfterMessageDeletion()
    {
        await using var f = new Fixture();
        var sender = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = f.Org, EmployeeType = EmployeeType.Agent, DisplayName = "Ada" };
        var chat = new Conversation { Id = Guid.NewGuid(), OrganizationId = f.Org };
        var message = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = chat.Id, SenderOrganizationUserId = sender.Id,
            Content = "Hello", CreatedAt = DateTimeOffset.UtcNow };
        f.Db.AddRange(sender, chat, message);
        var request = f.Db.DescribeAuditSource(f.Db.Entry(message))!;
        Assert.Contains(chat.Id.ToString(), request.MetadataJson);
        await f.Db.SaveChangesAsync();
        var eventId = await f.Writer.AppendAsync(request);
        var expected = $"/organizations/{f.Org:D}/communications/{chat.Id:D}?perspective={sender.Id:D}";
        var item = Assert.Single((await f.Reader.BrowseAsync(f.Org, new())).Items);
        Assert.Equal(expected, item.ActivityHref);
        f.Db.Remove(message); await f.Db.SaveChangesAsync();
        item = Assert.Single((await f.Reader.BrowseAsync(f.Org, new())).Items);
        Assert.Equal(expected, item.ActivityHref);
        Assert.Equal("Verified", (await f.Reader.GetAsync(f.Org, eventId))!.IntegrityStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkActivityLinksToCorrectSharedOrPersonalBoard(bool personal)
    {
        await using var f = new Fixture();
        var owner = Guid.NewGuid();
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = f.Org, Kind = personal ? WorkBoardKind.Personal : WorkBoardKind.Standard,
            OwnerOrganizationUserId = personal ? owner : null };
        var ticket = new WorkTask { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = board.Id, Title = "Build the game" };
        var model = new AgentRunLog { Id = Guid.NewGuid(), OrganizationId = f.Org, WorkItemId = ticket.Id, Status = "Completed" };
        f.Db.AddRange(board, ticket, model); await f.Db.SaveChangesAsync();
        await f.Writer.AppendAsync(new("model.call.completed", "Model", OrganizationId: f.Org, EntityType: "AgentRunLog", EntityId: model.Id));
        await f.Writer.AppendAsync(new("work.item.ready", "WorkItem", OrganizationId: f.Org, EntityType: "WorkTask", EntityId: ticket.Id));
        var expected = personal ? $"/organizations/{f.Org:D}/employees/{owner:D}?tab=personal-board&item={ticket.Id:D}"
            : $"/organizations/{f.Org:D}/work/boards/{board.Id:D}?item={ticket.Id:D}";
        Assert.All((await f.Reader.BrowseAsync(f.Org, new(GroupModelResponses: true))).Items, x => Assert.Equal(expected, x.ActivityHref));
    }

    [Fact]
    public async Task ToolTerminalInheritsRecordedWorkContextAfterExecutionFocusChanges()
    {
        await using var f = new Fixture();
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = f.Org };
        var ticket = new WorkTask { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = board.Id };
        var differentTicket = new WorkTask { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = board.Id };
        var context = new WorkExecutionContext { Id = Guid.NewGuid(), OrganizationId = f.Org,
            AgentWorkItemId = Guid.NewGuid(), RootWorkItemId = ticket.Id, WorkItemId = ticket.Id };
        f.Db.AddRange(board, ticket, differentTicket, context); await f.Db.SaveChangesAsync();
        var start = await f.Writer.AppendAsync(new("agent.capability.started", "AgentCapability", OrganizationId: f.Org,
            EntityType: "Capability", EntityId: context.Id, MetadataJson: JsonSerializer.Serialize(new { activityWorkItemId = ticket.Id })));
        await f.Writer.AppendAsync(new("agent.capability.executed", "AgentCapability", OrganizationId: f.Org,
            EntityType: "Capability", ParentEventId: start));
        context.WorkItemId = differentTicket.Id; await f.Db.SaveChangesAsync();
        Assert.All((await f.Reader.BrowseAsync(f.Org, new())).Items, x => Assert.Equal(
            $"/organizations/{f.Org:D}/work/boards/{board.Id:D}?item={ticket.Id:D}", x.ActivityHref));
    }

    [Fact]
    public async Task QueuedStageWorkLinksToItsTicketBeforeAnAttemptExists()
    {
        await using var f = new Fixture();
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = f.Org };
        var ticket = new WorkTask { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = board.Id };
        var execution = new WorkItemExecution { Id = Guid.NewGuid(), WorkItemId = ticket.Id };
        var stage = new WorkStageExecution { Id = Guid.NewGuid(), ItemExecutionId = execution.Id, AgentInstallationId = Guid.NewGuid() };
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString("D"), AgentInstallationId = stage.AgentInstallationId.Value,
            SourceType = "WorkStageExecution", SourceId = stage.Id.ToString("D"), Status = AgentWorkStatus.Pending };
        f.Db.AddRange(board, ticket, execution, stage, work);
        var request = f.Db.DescribeAuditSource(f.Db.Entry(work))!;
        Assert.Contains(ticket.Id.ToString("D"), request.MetadataJson);
        await f.Db.SaveChangesAsync();
        await f.Writer.AppendAsync(request);
        await f.Writer.AppendAsync(new("agent.work.pending", "AgentWork", OrganizationId: f.Org,
            EntityType: "AgentWorkItem", EntityId: work.Id));
        Assert.All((await f.Reader.BrowseAsync(f.Org, new())).Items, x => Assert.Equal(
            $"/organizations/{f.Org:D}/work/boards/{board.Id:D}?item={ticket.Id:D}", x.ActivityHref));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChatSendingToolUsesAgentPerspectiveEvenWhenAlsoBoundToWork(bool legacy)
    {
        await using var f = new Fixture();
        var sender = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = f.Org, EmployeeType = EmployeeType.Agent };
        var chat = new Conversation { Id = Guid.NewGuid(), OrganizationId = f.Org };
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = f.Org };
        var ticket = new WorkTask { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = board.Id };
        f.Db.AddRange(sender, chat, board, ticket); await f.Db.SaveChangesAsync();
        var start = await f.Writer.AppendAsync(new("agent.capability.started", "AgentCapability", OrganizationId: f.Org,
            EntityType: "Capability", Actor: new AuditActor("Agent", OrganizationUserId: sender.Id),
            MetadataJson: legacy ? "{\"toolName\":\"send_communication_message\"}" : JsonSerializer.Serialize(new {
                activityWorkItemId = ticket.Id, activityConversationId = chat.Id }),
            ContentType: "application/json", Payload: JsonSerializer.SerializeToUtf8Bytes(new { chatId = chat.Id, content = "Hello" })));
        await f.Writer.AppendAsync(new("agent.capability.executed", "AgentCapability", OrganizationId: f.Org,
            EntityType: "Capability", ParentEventId: start, Actor: new AuditActor("Agent", OrganizationUserId: sender.Id)));
        Assert.All((await f.Reader.BrowseAsync(f.Org, new())).Items, x => Assert.Equal(
            $"/organizations/{f.Org:D}/communications/{chat.Id:D}?perspective={sender.Id:D}", x.ActivityHref));
    }

    [Fact]
    public async Task UnrelatedTenantAndUnidentifiedAgentDoNotProduceMisleadingLinks()
    {
        await using var f = new Fixture();
        var chat = new Conversation { Id = Guid.NewGuid(), OrganizationId = f.Org };
        var foreignChat = new Conversation { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid() };
        var foreignItem = new WorkTask { Id = Guid.NewGuid(), OrganizationId = foreignChat.OrganizationId };
        f.Db.AddRange(chat, foreignChat, foreignItem); await f.Db.SaveChangesAsync();
        await f.Writer.AppendAsync(new("agent.capability.started", "AgentCapability", OrganizationId: f.Org,
            Actor: new AuditActor("Agent"), MetadataJson: JsonSerializer.Serialize(new { activityConversationId = chat.Id })));
        await f.Writer.AppendAsync(new("communication.message.sent", "Communication", OrganizationId: f.Org,
            MetadataJson: JsonSerializer.Serialize(new { activityConversationId = foreignChat.Id, activityWorkItemId = foreignItem.Id })));
        await f.Writer.AppendAsync(new("security.timeline.viewed", "SecurityAccess", OrganizationId: f.Org));
        Assert.All((await f.Reader.BrowseAsync(f.Org, new())).Items, x => Assert.Null(x.ActivityHref));
    }

    [Fact]
    public void OnlyChatSendingCapabilityExtractsConversationNavigationFromArguments()
    {
        var chatId = Guid.NewGuid();
        var args = JsonSerializer.SerializeToElement(new { chatId });
        var tool = new McpToolDescriptor("communication.message.send.v1", "send_communication_message", "Send a message",
            args, null, McpToolExecutionPolicy.AdvisoryWrite);
        Assert.Equal(chatId, McpGatewayEndpoints.CapabilityConversationId(tool, args));
        Assert.Null(McpGatewayEndpoints.CapabilityConversationId(tool with { Capability = "other" }, args));
        Assert.Null(McpGatewayEndpoints.CapabilityConversationId(tool, JsonSerializer.SerializeToElement(new { chatId = "invalid" })));
    }
}
