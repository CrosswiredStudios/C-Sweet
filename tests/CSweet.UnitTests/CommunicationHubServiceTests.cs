using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class CommunicationHubServiceTests
{
    [Fact]
    public async Task CreateGroup_ExpandsRoleAudienceAndPersistsMessages()
    {
        await using var db = CreateDb();
        var organization = Organization();
        var department = new Role { Id = Guid.NewGuid(), OrganizationId = organization.Id, Name = "Product",
            Description = "Product department", AuthorityLevel = AuthorityLevel.ExecutionWithApproval,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var manager = User(organization.Id, "Morgan", OrganizationPermissionLevel.Manager);
        var designer = User(organization.Id, "Drew", OrganizationPermissionLevel.Contributor, department.Id);
        var engineer = User(organization.Id, "Ellis", OrganizationPermissionLevel.Contributor, department.Id);
        db.AddRange(organization, department, manager, designer, engineer);
        await db.SaveChangesAsync();
        var audit = new TestAuditEventWriter();
        var service = new CommunicationHubService(
            db, audit, new CSweet.Infrastructure.Core.ChatTurnService(db));

        var created = await service.CreateAsync(organization.Id, manager.Id,
            new CreateCommunicationChatRequest("product-launch", "Launch coordination", false, false,
                [], [department.Id], []));

        Assert.True(created.Succeeded);
        Assert.Equal(3, created.Chat!.Participants.Count);
        Assert.Contains(created.Chat.Participants, x => x.OrganizationUserId == designer.Id);
        var sent = await service.SendAsync(organization.Id, created.Chat.Id, designer.Id,
            new SendCommunicationMessageRequest("Design review is ready.", "design-review-ready"));
        var replay = await service.SendAsync(organization.Id, created.Chat.Id, designer.Id,
            new SendCommunicationMessageRequest("Design review is ready.", "design-review-ready"));
        Assert.NotNull(sent);
        Assert.Equal(sent!.Message.Id, replay!.Message.Id);
        var messages = await service.ListMessagesAsync(organization.Id, created.Chat.Id, engineer.Id);
        Assert.NotNull(messages);
        Assert.Single(messages);
        Assert.Equal("Drew", messages[0].SenderDisplayName);
        var messageAudit = Assert.Single(
            audit.Events, x => x.EventType == "communication.message.sent");
        Assert.Equal(sent.Message.Id, messageAudit.EntityId);
        Assert.Equal(designer.Id, messageAudit.Actor?.OrganizationUserId);
        Assert.Contains(engineer.DisplayName, messageAudit.MetadataJson);
        Assert.DoesNotContain("Design review is ready.", messageAudit.MetadataJson);
    }

    [Fact]
    public async Task GroupManagement_IsScopedAndArchivePreservesHistory()
    {
        await using var db = CreateDb();
        var organization = Organization();
        var manager = User(organization.Id, "Manager", OrganizationPermissionLevel.Manager);
        var member = User(organization.Id, "Member", OrganizationPermissionLevel.Contributor);
        var outsider = User(organization.Id, "Outsider", OrganizationPermissionLevel.Contributor);
        db.AddRange(organization, manager, member, outsider);
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var created = await service.CreateAsync(organization.Id, manager.Id,
            new CreateCommunicationChatRequest("operations", null, false, true, [member.Id]));
        await service.SendAsync(organization.Id, created.Chat!.Id, member.Id, new SendCommunicationMessageRequest("Status update"));

        Assert.Null(await service.ListMessagesAsync(organization.Id, created.Chat.Id, outsider.Id));
        var denied = await service.ArchiveAsync(organization.Id, created.Chat.Id, outsider.Id);
        Assert.False(denied.Succeeded);
        Assert.Equal("not_authorized", denied.ErrorCode);

        var archived = await service.ArchiveAsync(organization.Id, created.Chat.Id, manager.Id);
        Assert.True(archived.Succeeded);
        Assert.NotNull((await db.CoreConversations.SingleAsync()).ArchivedAt);
        Assert.Single(await db.CoreConversationMessages.ToListAsync());
    }

    [Fact]
    public async Task Contributor_CanStartDirectMessageButCannotCreateGroup()
    {
        await using var db = CreateDb();
        var organization = Organization();
        var first = User(organization.Id, "First", OrganizationPermissionLevel.Contributor);
        var second = User(organization.Id, "Second", OrganizationPermissionLevel.Contributor);
        db.AddRange(organization, first, second);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var direct = await service.CreateAsync(organization.Id, first.Id,
            new CreateCommunicationChatRequest(null, null, true, true, [second.Id]));
        var group = await service.CreateAsync(organization.Id, first.Id,
            new CreateCommunicationChatRequest("Unauthorized", null, false, false, [second.Id]));

        Assert.True(direct.Succeeded);
        Assert.Equal("Second", direct.Chat!.Title);
        Assert.False(group.Succeeded);
        Assert.Equal("not_authorized", group.ErrorCode);
    }

    [Fact]
    public async Task UnreadSummary_ExcludesOwnMessagesAndAdvancesOnlyThroughDisplayedSequence()
    {
        await using var db = CreateDb();
        var organization = Organization();
        var first = User(organization.Id, "First", OrganizationPermissionLevel.Contributor);
        var second = User(organization.Id, "Second", OrganizationPermissionLevel.Contributor);
        db.AddRange(organization, first, second);
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var chat = (await service.CreateAsync(organization.Id, first.Id,
            new CreateCommunicationChatRequest(null, null, true, true, [second.Id]))).Chat!;
        var own = await service.SendAsync(organization.Id, chat.Id, first.Id, new("My message"));
        var received = await service.SendAsync(organization.Id, chat.Id, second.Id, new("Reply"));

        var unread = await service.GetUnreadSummaryAsync(organization.Id, first.Id);
        Assert.Equal(1, unread!.TotalUnreadCount);
        Assert.Equal(1, unread.ChatUnreadCounts[chat.Id]);

        var read = await service.MarkReadAsync(organization.Id, chat.Id, first.Id, received!.Message.Sequence);
        Assert.Equal(0, read!.TotalUnreadCount);
        Assert.True(own!.Message.Sequence < received.Message.Sequence);
        Assert.Contains(await db.CommunicationEventOutbox.ToListAsync(), x => x.EventType == CommunicationEvents.ReadUpdated);
    }

    [Fact]
    public async Task ProtectedAgentConversation_CannotBeModifiedOrArchived()
    {
        await using var db = CreateDb();
        var organization = Organization();
        var owner = User(organization.Id, "Owner", OrganizationPermissionLevel.Owner);
        var agent = User(organization.Id, "Programmer", OrganizationPermissionLevel.Contributor);
        agent.EmployeeType = EmployeeType.Agent;
        var chat = new Conversation
        {
            Id = Guid.NewGuid(), OrganizationId = organization.Id, InitiatedByOrganizationUserId = owner.Id,
            AgentOrganizationUserId = agent.Id, Kind = ConversationKind.DirectHumanAgent, IsPrivate = true,
            IsDeletionProtected = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        chat.Participants.Add(new() { Id = Guid.NewGuid(), OrganizationUserId = owner.Id, Role = ConversationParticipantRole.Coordinator, JoinedAt = DateTimeOffset.UtcNow });
        chat.Participants.Add(new() { Id = Guid.NewGuid(), OrganizationUserId = agent.Id, Role = ConversationParticipantRole.Member, JoinedAt = DateTimeOffset.UtcNow });
        db.AddRange(organization, owner, agent, chat);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var update = await service.UpdateAsync(organization.Id, chat.Id, owner.Id,
            new UpdateCommunicationChatRequest("Changed", null, true, [owner.Id, agent.Id]));
        var archive = await service.ArchiveAsync(organization.Id, chat.Id, owner.Id);

        Assert.Equal("protected_chat_immutable", update.ErrorCode);
        Assert.Equal("protected_chat_delete_denied", archive.ErrorCode);
        Assert.Null(chat.ArchivedAt);
    }

    [Fact]
    public async Task DirectHumanAgentMessage_QueuesDurableTurnAndReusesProtectedChat()
    {
        await using var db = CreateDb();
        var organization = Organization();
        var owner = User(organization.Id, "Owner", OrganizationPermissionLevel.Owner);
        var agent = User(organization.Id, "Operator", OrganizationPermissionLevel.Contributor);
        agent.EmployeeType = EmployeeType.Agent;
        db.AddRange(organization, owner, agent);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var first = await service.CreateAsync(organization.Id, owner.Id,
            new CreateCommunicationChatRequest(null, null, true, true, [agent.Id]));
        var second = await service.CreateAsync(organization.Id, owner.Id,
            new CreateCommunicationChatRequest(null, null, true, true, [agent.Id]));
        var sent = await service.SendAsync(organization.Id, first.Chat!.Id, owner.Id,
            new SendCommunicationMessageRequest("Prepare the report.", "report-request"));

        Assert.Equal(first.Chat.Id, second.Chat!.Id);
        Assert.True((await db.CoreConversations.SingleAsync()).IsDeletionProtected);
        Assert.NotNull(sent?.Turn);
        Assert.Equal(owner.Id, sent!.Message.SenderOrganizationUserId);
        Assert.Equal(sent.Turn!.Id, sent.Message.ChatTurnId);
        Assert.Equal(ChatTurnStatus.Queued, (await db.ChatTurns.SingleAsync()).Status);
        Assert.Single(await db.CoreConversationMessages.ToListAsync());
    }

    [Fact]
    public async Task GroupMessage_WithAgentParticipant_DoesNotQueueTurn()
    {
        await using var db = CreateDb();
        var organization = Organization();
        var manager = User(organization.Id, "Manager", OrganizationPermissionLevel.Manager);
        var agent = User(organization.Id, "Operator", OrganizationPermissionLevel.Contributor);
        agent.EmployeeType = EmployeeType.Agent;
        db.AddRange(organization, manager, agent);
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var chat = (await service.CreateAsync(organization.Id, manager.Id,
            new CreateCommunicationChatRequest("operations", null, false, false, [agent.Id]))).Chat!;

        var sent = await service.SendAsync(organization.Id, chat.Id, manager.Id,
            new SendCommunicationMessageRequest("Status update."));

        Assert.NotNull(sent);
        Assert.Null(sent!.Turn);
        Assert.Empty(await db.ChatTurns.ToListAsync());
    }

    private static Organization Organization() => new() { Id = Guid.NewGuid(), Name = "Example",
        Status = OrganizationStatus.Active, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
    private static OrganizationUser User(Guid organizationId, string name, OrganizationPermissionLevel permission, Guid? roleId = null) => new()
    {
        Id = Guid.NewGuid(), OrganizationId = organizationId, DisplayName = name, RoleId = roleId,
        EmployeeType = EmployeeType.Human, PermissionLevel = permission, IsActive = true, CreatedAt = DateTimeOffset.UtcNow
    };
    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static CommunicationHubService CreateService(CSweetDbContext db) =>
        new(db, new TestAuditEventWriter(), new CSweet.Infrastructure.Core.ChatTurnService(db));
}
