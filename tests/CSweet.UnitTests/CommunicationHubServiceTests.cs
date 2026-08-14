using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
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
    public async Task DirectAgentMessage_QueuesExactlyOneTurnForTheOtherAgentAndSupportsReverseDirection()
    {
        await using var db = CreateDb();
        var organization = Organization();
        var architect = User(organization.Id, "Software Architect", OrganizationPermissionLevel.Contributor);
        architect.EmployeeType = EmployeeType.Agent;
        var productManager = User(organization.Id, "Product Manager", OrganizationPermissionLevel.Manager);
        productManager.EmployeeType = EmployeeType.Agent;
        db.AddRange(organization, architect, productManager);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var chat = (await service.CreateAsync(
            organization.Id,
            architect.Id,
            new CreateCommunicationChatRequest(null, null, true, true, [productManager.Id]))).Chat!;
        var first = await service.SendAsync(
            organization.Id,
            chat.Id,
            architect.Id,
            new SendCommunicationMessageRequest("Begin delivery planning.", "architect-kickoff"));
        var duplicate = await service.SendAsync(
            organization.Id,
            chat.Id,
            architect.Id,
            new SendCommunicationMessageRequest("Begin delivery planning.", "architect-kickoff"));

        Assert.NotNull(first?.Turn);
        Assert.Equal(productManager.Id, first!.Turn!.TargetAgentOrganizationUserId);
        Assert.Equal(first.Message.Id, duplicate!.Message.Id);
        Assert.Equal(first.Turn.Id, duplicate.Turn!.Id);
        var firstTurn = await db.ChatTurns.SingleAsync();
        firstTurn.Status = ChatTurnStatus.Completed;
        firstTurn.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var reverse = await service.SendAsync(
            organization.Id,
            chat.Id,
            productManager.Id,
            new SendCommunicationMessageRequest("Clarify the first increment.", "pm-clarification"));

        Assert.NotNull(reverse?.Turn);
        Assert.Equal(architect.Id, reverse!.Turn!.TargetAgentOrganizationUserId);
        Assert.Equal(2, await db.ChatTurns.CountAsync());
        Assert.Equal(2, await db.CoreConversationMessages.CountAsync());
    }

    [Fact]
    public async Task DirectAgentToHumanMessage_RemainsInformational()
    {
        await using var db = CreateDb();
        var organization = Organization();
        var agent = User(organization.Id, "Product Manager", OrganizationPermissionLevel.Manager);
        agent.EmployeeType = EmployeeType.Agent;
        var manager = User(organization.Id, "Manager", OrganizationPermissionLevel.Manager);
        db.AddRange(organization, agent, manager);
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var chat = (await service.CreateAsync(
            organization.Id,
            agent.Id,
            new CreateCommunicationChatRequest(null, null, true, true, [manager.Id]))).Chat!;

        var sent = await service.SendAsync(
            organization.Id,
            chat.Id,
            agent.Id,
            new SendCommunicationMessageRequest("A manager decision is required."));

        Assert.NotNull(sent);
        Assert.Null(sent!.Turn);
        Assert.Empty(await db.ChatTurns.ToListAsync());
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

    [Fact]
    public async Task GetAsync_ReportsSuppressedAgentAsUnhealthy()
    {
        await using var db = CreateDb();
        var organization = Organization();
        var owner = User(organization.Id, "Owner", OrganizationPermissionLevel.Owner);
        var agent = User(organization.Id, "Product Manager", OrganizationPermissionLevel.Contributor);
        agent.EmployeeType = EmployeeType.Agent;
        var installation = new AgentInstallation
        {
            Id = Guid.NewGuid(),
            InstallationKey = Guid.NewGuid(),
            PackageVersionId = Guid.NewGuid(),
            BusinessId = organization.Id.ToString("D"),
            IsEnabled = true,
            RevisionStatus = PluginRevisionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        installation.Schedule = new AgentSchedule
        {
            Id = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            ActivationMode = ActivationMode.AlwaysOn,
            IsEnabled = true,
            ConsecutiveStartupFailures = 3,
            AutomaticStartSuppressedAt = DateTimeOffset.UtcNow
        };
        var runtime = new AgentRuntimeInstance
        {
            Id = Guid.NewGuid(),
            TickId = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        runtime.TransitionTo(
            AgentRuntimeStatus.Failed,
            DateTimeOffset.UtcNow,
            "The authenticated guest broker protocol was rejected.");
        agent.AgentInstallationId = installation.Id;
        agent.AgentInstallation = installation;
        db.AddRange(organization, owner, agent, installation, runtime);
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var direct = await service.CreateAsync(
            organization.Id,
            owner.Id,
            new CreateCommunicationChatRequest(null, null, true, true, [agent.Id]));

        var directChat = Assert.IsType<CommunicationChatResponse>(direct.Chat);
        var hub = Assert.IsType<CommunicationHubResponse>(
            await service.GetAsync(organization.Id, owner.Id));

        var person = Assert.Single(hub.People, x => x.Id == agent.Id);
        Assert.Equal(CommunicationPresenceStatuses.Unhealthy, person.PresenceStatus);
        Assert.Contains("suppressed", person.PresenceDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("guest broker protocol", person.PresenceDetail, StringComparison.OrdinalIgnoreCase);
        var refreshedChat = Assert.Single(hub.Chats, x => x.Id == directChat.Id);
        var participant = Assert.Single(refreshedChat.Participants, x => x.OrganizationUserId == agent.Id);
        Assert.Equal(CommunicationPresenceStatuses.Unhealthy, participant.PresenceStatus);
        Assert.Contains("guest broker protocol", participant.PresenceDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAsync_ReportsFreshAlwaysOnAgentAsStartingBeforeRuntimeExists()
    {
        await using var db = CreateDb();
        var organization = Organization();
        var owner = User(organization.Id, "Owner", OrganizationPermissionLevel.Owner);
        var agent = User(organization.Id, "Product Manager", OrganizationPermissionLevel.Contributor);
        agent.EmployeeType = EmployeeType.Agent;
        var installation = new AgentInstallation
        {
            Id = Guid.NewGuid(),
            InstallationKey = Guid.NewGuid(),
            PackageVersionId = Guid.NewGuid(),
            BusinessId = organization.Id.ToString("D"),
            IsEnabled = true,
            RevisionStatus = PluginRevisionStatus.Active,
            SetupState = PluginSetupState.Ready,
            ConfigurationSyncStatus = AgentConfigurationSyncStatus.PendingNextStart,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        installation.Schedule = new AgentSchedule
        {
            Id = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            ActivationMode = ActivationMode.AlwaysOn,
            IsEnabled = true
        };
        agent.AgentInstallationId = installation.Id;
        agent.AgentInstallation = installation;
        db.AddRange(organization, owner, agent, installation);
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var direct = await service.CreateAsync(
            organization.Id,
            owner.Id,
            new CreateCommunicationChatRequest(null, null, true, true, [agent.Id]));

        var directChat = Assert.IsType<CommunicationChatResponse>(direct.Chat);
        var hub = Assert.IsType<CommunicationHubResponse>(
            await service.GetAsync(organization.Id, owner.Id));

        var person = Assert.Single(hub.People, x => x.Id == agent.Id);
        Assert.Equal(CommunicationPresenceStatuses.Starting, person.PresenceStatus);
        Assert.Contains("activation", person.PresenceDetail, StringComparison.OrdinalIgnoreCase);
        var refreshedChat = Assert.Single(hub.Chats, x => x.Id == directChat.Id);
        var participant = Assert.Single(refreshedChat.Participants, x => x.OrganizationUserId == agent.Id);
        Assert.Equal(CommunicationPresenceStatuses.Starting, participant.PresenceStatus);
    }

    [Fact]
    public async Task GetAsync_ReportsRunningAgentAsAvailableWhileConfigurationRefreshes()
    {
        await using var db = CreateDb();
        var organization = Organization();
        var owner = User(organization.Id, "Owner", OrganizationPermissionLevel.Owner);
        var agent = User(organization.Id, "Product Manager", OrganizationPermissionLevel.Contributor);
        agent.EmployeeType = EmployeeType.Agent;
        var installation = new AgentInstallation
        {
            Id = Guid.NewGuid(),
            InstallationKey = Guid.NewGuid(),
            PackageVersionId = Guid.NewGuid(),
            BusinessId = organization.Id.ToString("D"),
            IsEnabled = true,
            RevisionStatus = PluginRevisionStatus.Active,
            SetupState = PluginSetupState.Ready,
            ConfigurationSyncStatus = AgentConfigurationSyncStatus.Refreshing,
            DesiredConfigurationRevision = 2,
            AppliedConfigurationRevision = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        installation.Schedule = new AgentSchedule
        {
            Id = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            ActivationMode = ActivationMode.AlwaysOn,
            IsEnabled = true
        };
        var runtime = new AgentRuntimeInstance
        {
            Id = Guid.NewGuid(),
            TickId = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            RuntimeDeadlineAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        runtime.TransitionTo(AgentRuntimeStatus.Starting, DateTimeOffset.UtcNow.AddMinutes(-1));
        runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, DateTimeOffset.UtcNow.AddMinutes(-1));
        runtime.TransitionTo(AgentRuntimeStatus.Running, DateTimeOffset.UtcNow.AddMinutes(-1));
        agent.AgentInstallationId = installation.Id;
        agent.AgentInstallation = installation;
        db.AddRange(organization, owner, agent, installation, runtime);
        await db.SaveChangesAsync();
        var service = CreateService(db);
        await service.CreateAsync(
            organization.Id,
            owner.Id,
            new CreateCommunicationChatRequest(null, null, true, true, [agent.Id]));

        var hub = Assert.IsType<CommunicationHubResponse>(
            await service.GetAsync(organization.Id, owner.Id));

        var person = Assert.Single(hub.People, x => x.Id == agent.Id);
        Assert.Equal(CommunicationPresenceStatuses.Available, person.PresenceStatus);
        var chat = Assert.Single(hub.Chats, x => x.IsDirect);
        var participant = Assert.Single(chat.Participants, x => x.OrganizationUserId == agent.Id);
        Assert.Equal(CommunicationPresenceStatuses.Available, participant.PresenceStatus);
    }

    [Fact]
    public async Task GetAsync_ReconcilesMissingDirectConversationForExistingAgent()
    {
        await using var db = CreateDb();
        var organization = Organization();
        var owner = User(organization.Id, "Owner", OrganizationPermissionLevel.Owner);
        owner.ApplicationUserId = Guid.NewGuid();
        var agent = User(organization.Id, "Product Manager", OrganizationPermissionLevel.Contributor);
        agent.EmployeeType = EmployeeType.Agent;
        agent.ReportsToOrganizationUserId = owner.Id;
        var installation = new AgentInstallation
        {
            Id = Guid.NewGuid(),
            InstallationKey = Guid.NewGuid(),
            PackageVersionId = Guid.NewGuid(),
            BusinessId = organization.Id.ToString("D"),
            IsEnabled = true,
            RevisionStatus = PluginRevisionStatus.Active,
            SetupState = PluginSetupState.Ready,
            ConfigurationSyncStatus = AgentConfigurationSyncStatus.PendingNextStart,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        installation.Schedule = new AgentSchedule
        {
            Id = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            ActivationMode = ActivationMode.AlwaysOn,
            IsEnabled = true
        };
        agent.AgentInstallationId = installation.Id;
        agent.AgentInstallation = installation;
        db.AddRange(organization, owner, agent, installation);
        await db.SaveChangesAsync();
        var service = new CommunicationHubService(
            db,
            new TestAuditEventWriter(),
            new CSweet.Infrastructure.Core.ChatTurnService(db),
            onboarding: new AgentCommunicationOnboardingService(db));

        var hub = Assert.IsType<CommunicationHubResponse>(
            await service.GetAsync(organization.Id, owner.Id));

        var chat = Assert.Single(hub.Chats, item => item.IsDirect);
        Assert.Equal("Product Manager", chat.Title);
        Assert.Equal(CommunicationPresenceStatuses.Starting,
            Assert.Single(chat.Participants, participant => participant.OrganizationUserId == agent.Id).PresenceStatus);
        Assert.Single(await db.CoreConversations.ToListAsync());
        Assert.Single(await db.AgentOnboardingEventOutbox.ToListAsync());
    }

    [Fact]
    public async Task HumanCanInspectAgentPerspective_WithoutGainingAgentMutationAuthority()
    {
        await using var db = CreateDb();
        var organization = Organization();
        var owner = User(organization.Id, "Owner", OrganizationPermissionLevel.Owner);
        var productManager = User(
            organization.Id, "Product Manager", OrganizationPermissionLevel.Manager);
        productManager.EmployeeType = EmployeeType.Agent;
        var architect = User(
            organization.Id, "Software Architect", OrganizationPermissionLevel.Contributor);
        architect.EmployeeType = EmployeeType.Agent;
        var otherHuman = User(
            organization.Id, "Other Human", OrganizationPermissionLevel.Contributor);
        db.AddRange(organization, owner, productManager, architect, otherHuman);
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var direct = await service.CreateAsync(
            organization.Id,
            productManager.Id,
            new CreateCommunicationChatRequest(
                null, null, true, true, [architect.Id]));
        await service.SendAsync(
            organization.Id,
            direct.Chat!.Id,
            productManager.Id,
            new SendCommunicationMessageRequest("Please review the delivery architecture."));
        await service.SendAsync(
            organization.Id,
            direct.Chat.Id,
            architect.Id,
            new SendCommunicationMessageRequest("Review complete."));

        var self = Assert.IsType<CommunicationHubResponse>(
            await service.GetAsync(organization.Id, owner.Id));
        var perspective = Assert.IsType<CommunicationHubResponse>(
            await service.GetAsync(organization.Id, owner.Id, productManager.Id));

        Assert.Empty(self.Chats);
        Assert.Equal(owner.Id, perspective.CurrentOrganizationUserId);
        Assert.Equal(productManager.Id, perspective.ViewedOrganizationUserId);
        Assert.True(perspective.IsReadOnlyPerspective);
        Assert.False(perspective.CanManageChats);
        var inspectedChat = Assert.Single(perspective.Chats);
        Assert.Equal("Software Architect", inspectedChat.Title);
        Assert.False(inspectedChat.CanManage);
        var messages = Assert.IsAssignableFrom<IReadOnlyList<CommunicationHubMessageResponse>>(
            await service.ListMessagesAsync(
                organization.Id,
                inspectedChat.Id,
                owner.Id,
                productManager.Id));
        Assert.Equal(2, messages.Count);

        Assert.Null(await service.ListMessagesAsync(
            organization.Id, inspectedChat.Id, owner.Id));
        Assert.Null(await service.GetAsync(
            organization.Id, owner.Id, otherHuman.Id));
        Assert.Null(await service.GetAsync(
            organization.Id, owner.Id, Guid.NewGuid()));
    }

    [Fact]
    public async Task StructuredMentionsPersistAndNotifyOnlyActiveParticipantsOncePerIdentity()
    {
        await using var db = CreateDb();
        var organization = Organization();
        var sender = User(organization.Id, "Morgan", OrganizationPermissionLevel.Manager);
        var agent = User(organization.Id, "Henry", OrganizationPermissionLevel.Contributor);
        agent.EmployeeType = EmployeeType.Agent;
        agent.AgentInstallationId = Guid.NewGuid();
        var human = User(organization.Id, "Harriet", OrganizationPermissionLevel.Contributor);
        var outsider = User(organization.Id, "Nora", OrganizationPermissionLevel.Contributor);
        db.AddRange(organization, sender, agent, human, outsider);
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var chat = (await service.CreateAsync(organization.Id, sender.Id,
            new CreateCommunicationChatRequest("Mentions", null, false, false,
                [agent.Id, human.Id]))).Chat!;
        const string content = "Hi @Henry, please sync with @Harriet. FYI @Nora. Again @Henry.";
        var firstHenry = content.IndexOf("@Henry", StringComparison.Ordinal);
        var harriet = content.IndexOf("@Harriet", StringComparison.Ordinal);
        var nora = content.IndexOf("@Nora", StringComparison.Ordinal);
        var secondHenry = content.LastIndexOf("@Henry", StringComparison.Ordinal);

        await service.SendAsync(organization.Id, chat.Id, sender.Id,
            new SendCommunicationMessageRequest(content, "mentions", [
                new(agent.Id, firstHenry, "@Henry".Length),
                new(human.Id, harriet, "@Harriet".Length),
                new(outsider.Id, nora, "@Nora".Length),
                new(agent.Id, secondHenry, "@Henry".Length)
            ]));

        Assert.Equal(4, await db.ConversationMessageMentions.CountAsync());
        var agentEvent = Assert.Single(await db.AgentPlatformEventOutbox.Where(x =>
            x.EventType == CommunicationEvents.MessageMentioned).ToListAsync());
        Assert.Equal(agent.AgentInstallationId, agentEvent.TargetInstallationId);
        var notification = Assert.Single(await db.UserNotifications.Where(x =>
            x.Category == "Mention").ToListAsync());
        Assert.Equal(human.Id, notification.RecipientOrganizationUserId);
        Assert.DoesNotContain(await db.UserNotifications.ToListAsync(), x =>
            x.RecipientOrganizationUserId == outsider.Id);
    }

    [Fact]
    public async Task StructuredMentionsRejectInvisibleOverlappingAndCrossOrganizationIdentities()
    {
        await using var db = CreateDb();
        var organization = Organization();
        var otherOrganization = Organization();
        var sender = User(organization.Id, "Morgan", OrganizationPermissionLevel.Manager);
        var recipient = User(organization.Id, "Henry", OrganizationPermissionLevel.Contributor);
        var foreign = User(otherOrganization.Id, "Henry", OrganizationPermissionLevel.Contributor);
        db.AddRange(organization, otherOrganization, sender, recipient, foreign);
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var chat = (await service.CreateAsync(organization.Id, sender.Id,
            new CreateCommunicationChatRequest(null, null, true, true, [recipient.Id]))).Chat!;

        await Assert.ThrowsAsync<ArgumentException>(() => service.SendAsync(
            organization.Id, chat.Id, sender.Id,
            new SendCommunicationMessageRequest("Hello Henry", null,
                [new(recipient.Id, 6, 5)])));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SendAsync(
            organization.Id, chat.Id, sender.Id,
            new SendCommunicationMessageRequest("@Henry", null,
                [new(recipient.Id, 0, 6), new(recipient.Id, 1, 5)])));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SendAsync(
            organization.Id, chat.Id, sender.Id,
            new SendCommunicationMessageRequest("@Henry", null,
                [new(foreign.Id, 0, 6)])));
        Assert.Empty(await db.CoreConversationMessages.ToListAsync());
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
