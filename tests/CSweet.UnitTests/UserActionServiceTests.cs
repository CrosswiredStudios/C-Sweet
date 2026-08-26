using System.Text.Json;
using CSweet.Application.Communications;
using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class UserActionServiceTests
{
    [Fact]
    public async Task HiringAction_IsOwnedByOriginatingAgentAndResolvesOnlyServerRoute()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var organization = new Organization
        {
            Id = Guid.NewGuid(), Name = "Example", CreatedAt = now, UpdatedAt = now
        };
        var installationId = Guid.NewGuid();
        var agent = new OrganizationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = organization.Id,
            AgentInstallationId = installationId,
            DisplayName = "Chief",
            EmployeeType = EmployeeType.Agent,
            PermissionLevel = OrganizationPermissionLevel.Manager,
            CreatedAt = now
        };
        var other = new OrganizationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = organization.Id,
            AgentInstallationId = Guid.NewGuid(),
            DisplayName = "Other",
            EmployeeType = EmployeeType.Agent,
            PermissionLevel = OrganizationPermissionLevel.Contributor,
            CreatedAt = now
        };
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            OrganizationId = organization.Id,
            Title = "CEO",
            CreatedAt = now,
            UpdatedAt = now
        };
        conversation.Participants.Add(new ConversationParticipant
        {
            Id = Guid.NewGuid(),
            OrganizationUserId = agent.Id,
            Role = ConversationParticipantRole.Member,
            JoinedAt = now
        });
        conversation.Participants.Add(new ConversationParticipant
        {
            Id = Guid.NewGuid(),
            OrganizationUserId = other.Id,
            Role = ConversationParticipantRole.Member,
            JoinedAt = now
        });
        var message = new ConversationMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            Conversation = conversation,
            SenderOrganizationUserId = agent.Id,
            Content = "Hire a Product Manager.",
            Role = ConversationRole.Assistant,
            CorrelationId = Guid.NewGuid(),
            CreatedAt = now
        };
        var otherMessage = new ConversationMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            Conversation = conversation,
            SenderOrganizationUserId = other.Id,
            Content = "Other",
            Role = ConversationRole.Assistant,
            CorrelationId = Guid.NewGuid(),
            CreatedAt = now
        };
        db.AddRange(organization, agent, other, conversation, message, otherMessage);
        await db.SaveChangesAsync();
        var service = new UserActionService(
            db,
            new IUserActionWorkflowResolver[] { new HiringMarketplaceUserActionWorkflowResolver() });

        var recommendationId = Guid.NewGuid();
        var action = await service.SuggestAsync(
            organization.Id,
            installationId,
            new SuggestUserActionRequest(
                message.Id,
                null,
                SuggestedUserActionWorkflows.BrowseHiringMarketplace,
                "Browse candidates",
                "Review Marketplace candidates for the Product Manager role.",
                JsonSerializer.SerializeToElement(new
                {
                    role = "Product Manager",
                    recommendationId,
                    url = "https://attacker.example"
                }),
                "product-manager-action"));

        Assert.Equal(
            $"/organizations/{organization.Id:D}/marketplace?role=Product%20Manager&recommendationId={recommendationId:D}",
            action.NavigationUri);
        Assert.DoesNotContain("attacker", action.NavigationUri, StringComparison.OrdinalIgnoreCase);
        var persistedAction = Assert.Single(await db.SuggestedUserActions.ToListAsync());
        var systemMessage = Assert.Single(
            await db.CoreConversationMessages
                .Where(x => x.SourceProvider == CommunicationMessageTypes.SystemAction)
                .ToListAsync());
        Assert.Equal(systemMessage.Id, persistedAction.ConversationMessageId);
        Assert.Null(persistedAction.ChatTurnId);
        Assert.Null(systemMessage.SenderOrganizationUserId);
        Assert.Equal(ConversationRole.Assistant, systemMessage.Role);
        Assert.Equal("Review Marketplace candidates for the Product Manager role.", systemMessage.Content);
        Assert.Equal(message.Id, systemMessage.CausationId);
        Assert.Single(
            await db.ApplicationRealtimeOutbox.ToListAsync(),
            x => x.EventType == "com.csweet.communication.user-action.created.v1");
        var hub = new CommunicationHubService(
            db,
            new TestAuditEventWriter(),
            new CSweet.Infrastructure.Core.ChatTurnService(db));
        var responses = await hub.ListMessagesAsync(organization.Id, conversation.Id, other.Id);
        var systemResponse = Assert.Single(
            responses!,
            x => x.MessageType == CommunicationMessageTypes.SystemAction);
        Assert.Equal("C-Sweet", systemResponse.SenderDisplayName);
        Assert.Equal("System", systemResponse.SenderEmployeeType);
        Assert.Equal(action.Id, Assert.Single(systemResponse.Actions!).Id);

        persistedAction.Status = "Completed";
        persistedAction.ResultOrganizationUserId = other.Id;
        persistedAction.CompletedAt = now.AddMinutes(1);
        await db.SaveChangesAsync();
        responses = await hub.ListMessagesAsync(organization.Id, conversation.Id, other.Id);
        var completedResponse = Assert.Single(
            Assert.Single(responses!, x => x.MessageType == CommunicationMessageTypes.SystemAction).Actions!);
        Assert.Equal("Completed", completedResponse.Status);
        Assert.Equal(other.Id, completedResponse.ResultOrganizationUserId);
        Assert.Equal(other.DisplayName, completedResponse.ResultOrganizationUserDisplayName);
        Assert.Equal(persistedAction.CompletedAt, completedResponse.CompletedAt);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SuggestAsync(
            organization.Id,
            installationId,
            new SuggestUserActionRequest(
                otherMessage.Id,
                null,
                SuggestedUserActionWorkflows.BrowseHiringMarketplace,
                "Browse candidates",
                null,
                JsonSerializer.SerializeToElement(new { role = "Engineering Manager" }),
                "other-message-action")));
    }

    [Fact]
    public async Task ChatTurnAction_IsMaterializedAfterCompletedAssistantMessage()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var organization = new Organization { Id = Guid.NewGuid(), Name = "Example", CreatedAt = now, UpdatedAt = now };
        var installationId = Guid.NewGuid();
        var agent = new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organization.Id, AgentInstallationId = installationId,
            DisplayName = "Chief", EmployeeType = EmployeeType.Agent,
            PermissionLevel = OrganizationPermissionLevel.Manager, CreatedAt = now
        };
        var owner = new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organization.Id, DisplayName = "Owner",
            EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner, CreatedAt = now
        };
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(), OrganizationId = organization.Id, AgentOrganizationUserId = agent.Id,
            InitiatedByOrganizationUserId = owner.Id, Kind = ConversationKind.DirectHumanAgent,
            CreatedAt = now, UpdatedAt = now
        };
        conversation.Participants.Add(new ConversationParticipant
        {
            Id = Guid.NewGuid(), OrganizationUserId = agent.Id, Role = ConversationParticipantRole.Member, JoinedAt = now
        });
        conversation.Participants.Add(new ConversationParticipant
        {
            Id = Guid.NewGuid(), OrganizationUserId = owner.Id, Role = ConversationParticipantRole.Member, JoinedAt = now
        });
        var turnId = Guid.NewGuid();
        var userMessage = new ConversationMessage
        {
            Id = Guid.NewGuid(), ConversationId = conversation.Id, ChatTurnId = turnId,
            SenderOrganizationUserId = owner.Id, Role = ConversationRole.User, Content = "Who should I hire?",
            CorrelationId = Guid.NewGuid(), CreatedAt = now
        };
        var turn = new ChatTurn
        {
            Id = turnId, OrganizationId = organization.Id, ConversationId = conversation.Id,
            TargetAgentOrganizationUserId = agent.Id, UserMessageId = userMessage.Id,
            Status = ChatTurnStatus.Running, CreatedAt = now, UpdatedAt = now
        };
        db.AddRange(organization, agent, owner, conversation, userMessage, turn);
        await db.SaveChangesAsync();
        var service = new UserActionService(
            db,
            new IUserActionWorkflowResolver[] { new HiringMarketplaceUserActionWorkflowResolver() });

        var suggested = await service.SuggestAsync(
            organization.Id,
            installationId,
            new SuggestUserActionRequest(
                null,
                turnId,
                SuggestedUserActionWorkflows.BrowseHiringMarketplace,
                "Browse candidates",
                "Review Marketplace candidates for the Creative Director role.",
                JsonSerializer.SerializeToElement(new { role = "Creative Director" }),
                "creative-director-action"));

        var pending = Assert.Single(await db.SuggestedUserActions.ToListAsync());
        Assert.Equal(turnId, pending.ChatTurnId);
        Assert.Null(pending.ConversationMessageId);
        Assert.Empty(await db.CoreConversationMessages.Where(x =>
            x.SourceProvider == CommunicationMessageTypes.SystemAction).ToListAsync());

        var assistant = new ConversationMessage
        {
            Id = Guid.NewGuid(), ConversationId = conversation.Id, ChatTurnId = turnId,
            SenderOrganizationUserId = agent.Id, Role = ConversationRole.Assistant,
            Content = "I recommend a Creative Director.", CorrelationId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow
        };
        db.CoreConversationMessages.Add(assistant);
        await db.SaveChangesAsync();
        await new CSweet.Infrastructure.Core.ChatTurnService(db).CompleteAsync(turnId, assistant.Id, false);

        var systemMessage = Assert.Single(await db.CoreConversationMessages.Where(x =>
            x.SourceProvider == CommunicationMessageTypes.SystemAction).ToListAsync());
        await db.Entry(pending).ReloadAsync();
        Assert.Equal(systemMessage.Id, pending.ConversationMessageId);
        Assert.Null(pending.ChatTurnId);
        Assert.True(systemMessage.CreatedAt >= assistant.CreatedAt);

        var hub = new CommunicationHubService(
            db,
            new TestAuditEventWriter(),
            new CSweet.Infrastructure.Core.ChatTurnService(db));
        var messages = (await hub.ListMessagesAsync(organization.Id, conversation.Id, owner.Id))!.ToList();
        Assert.True(messages.FindIndex(x => x.Id == assistant.Id) < messages.FindIndex(x => x.Id == systemMessage.Id));
        Assert.Equal(suggested.Id, Assert.Single(messages.Single(x => x.Id == systemMessage.Id).Actions!).Id);
    }

    private static CSweetDbContext CreateDb() => new(
        new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
