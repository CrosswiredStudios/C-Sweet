using CSweet.Contracts.Core;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ResourceChangeServiceTests
{
    [Fact]
    public async Task InitialProposal_IsAtomicIdempotentAndTargetsCurrentManagerInstallation()
    {
        await using var db = CreateDb();
        var setup = SeedManagerConversation(db);
        await db.SaveChangesAsync();
        var service = new ResourceChangeService(db, new TestAuditEventWriter());
        var proposal = Proposal(setup, "initial-team");

        var first = await service.ProposeAsync(setup.OrganizationId, setup.RequesterInstallationId, proposal);
        var retry = await service.ProposeAsync(setup.OrganizationId, setup.RequesterInstallationId, proposal);

        Assert.Equal(first.Id, retry.Id);
        Assert.All(first.Deltas, x => Assert.Equal("Add", x.ChangeKind));
        Assert.Equal(2, first.Roles.Count);
        Assert.Single(await db.ResourceChangeRequests.ToListAsync());
        Assert.Single(await db.CoreConversationMessages.Where(x =>
            x.SourceProvider == ResourceChangeService.MessageSource).ToListAsync());
        var requested = Assert.Single(await db.AgentPlatformEventOutbox.Where(x =>
            x.EventType == ResourceChangeEvents.Requested).ToListAsync());
        Assert.Equal(setup.ManagerInstallationId, requested.TargetInstallationId);
    }

    [Fact]
    public async Task ProposalFromNonManagerTurn_IsDenied()
    {
        await using var db = CreateDb();
        var setup = SeedManagerConversation(db);
        setup.UserMessage.SenderOrganizationUserId = setup.RequesterId;
        await db.SaveChangesAsync();
        var service = new ResourceChangeService(db, new TestAuditEventWriter());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ProposeAsync(setup.OrganizationId, setup.RequesterInstallationId, Proposal(setup, "invalid")));
        Assert.Empty(await db.ResourceChangeRequests.ToListAsync());
    }

    [Fact]
    public async Task AgentManagedProposal_WithoutManagerTurn_TargetsCurrentManagerInstallation()
    {
        await using var db = CreateDb();
        var setup = SeedManagerConversation(db);
        await db.SaveChangesAsync();
        var service = new ResourceChangeService(db, new TestAuditEventWriter());

        var request = await service.ProposeAsync(
            setup.OrganizationId,
            setup.RequesterInstallationId,
            Proposal(setup, "agent-manager-cross-conversation") with
            {
                ChatTurnId = Guid.Empty
            });

        Assert.Equal(Guid.Empty, request.ChatTurnId);
        Assert.Equal("QueuedForManagerAgent", request.DeliveryStatus);
        var requested = Assert.Single(await db.AgentPlatformEventOutbox.Where(x =>
            x.EventType == ResourceChangeEvents.Requested).ToListAsync());
        Assert.Equal(setup.ManagerInstallationId, requested.TargetInstallationId);
        Assert.Contains(request.Id.ToString("D"), requested.DataJson);
    }

    [Fact]
    public async Task FormerManagerCannotDecidePendingRequest()
    {
        await using var db = CreateDb();
        var setup = SeedManagerConversation(db);
        await db.SaveChangesAsync();
        var service = new ResourceChangeService(db, new TestAuditEventWriter());
        var request = await service.ProposeAsync(
            setup.OrganizationId,
            setup.RequesterInstallationId,
            Proposal(setup, "manager-change"));
        var replacement = User(setup.OrganizationId, "New manager", EmployeeType.Human);
        db.CoreOrganizationUsers.Add(replacement);
        var requester = await db.CoreOrganizationUsers.SingleAsync(x => x.Id == setup.RequesterId);
        requester.ReportsToOrganizationUserId = replacement.Id;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.DecideForInstallationAsync(
                setup.OrganizationId,
                setup.ManagerInstallationId,
                new ResourceChangeDecisionRequest(request.Id, ResourceChangeDecisionKinds.Approve, null, "stale-manager")));
    }

    [Fact]
    public async Task ApprovedRevision_PreservesFullSnapshotAndEmitsOnlyMeaningfulDeltas()
    {
        await using var db = CreateDb();
        var setup = SeedManagerConversation(db);
        await db.SaveChangesAsync();
        var service = new ResourceChangeService(db, new TestAuditEventWriter());
        var initial = await service.ProposeAsync(
            setup.OrganizationId,
            setup.RequesterInstallationId,
            Proposal(setup, "snapshot-1"));
        await service.DecideForInstallationAsync(
            setup.OrganizationId,
            setup.ManagerInstallationId,
            new ResourceChangeDecisionRequest(initial.Id, ResourceChangeDecisionKinds.Approve, null, "approve-1"));
        var increasedRoles = initial.Roles.Select(x =>
            x.RoleKey == "quality" ? x with { Headcount = 2 } : x).ToList();

        var revision = await service.ProposeAsync(
            setup.OrganizationId,
            setup.RequesterInstallationId,
            Proposal(setup, "snapshot-2") with
            {
                Roles = increasedRoles,
                SupersedesRequestId = initial.Id
            });

        Assert.Equal(2, revision.Roles.Count);
        var delta = Assert.Single(revision.Deltas);
        Assert.Equal("Increase", delta.ChangeKind);
        Assert.Equal("quality", delta.Role.RoleKey);
    }

    [Fact]
    public async Task ApprovedDecision_BroadcastsDecisionToAllSubscribedInstallations()
    {
        await using var db = CreateDb();
        var setup = SeedManagerConversation(db);
        await db.SaveChangesAsync();
        var service = new ResourceChangeService(db, new TestAuditEventWriter());
        var request = await service.ProposeAsync(
            setup.OrganizationId,
            setup.RequesterInstallationId,
            Proposal(setup, "approval-broadcast"));

        var approved = await service.DecideForInstallationAsync(
            setup.OrganizationId,
            setup.ManagerInstallationId,
            new ResourceChangeDecisionRequest(
                request.Id,
                ResourceChangeDecisionKinds.Approve,
                "Approved.",
                "approval-broadcast-decision"));

        Assert.Equal("Approved", approved.Status);
        var decided = Assert.Single(await db.AgentPlatformEventOutbox.Where(x =>
            x.EventType == ResourceChangeEvents.Decided).ToListAsync());
        Assert.Null(decided.TargetInstallationId);
        Assert.Contains(request.Id.ToString("D"), decided.DataJson);
        Assert.Contains("\"status\":\"Approved\"", decided.DataJson);
    }

    [Fact]
    public async Task ApprovedDecision_GrantsRequesterBoardCreationOnce_WhenPackageRequiresCapability()
    {
        await using var db = CreateDb();
        var setup = SeedManagerConversation(db);
        SeedRequesterInstallation(db, setup, [WorkBoardActions.Create]);
        await db.SaveChangesAsync();
        var service = new ResourceChangeService(db, new TestAuditEventWriter());
        var request = await service.ProposeAsync(
            setup.OrganizationId,
            setup.RequesterInstallationId,
            Proposal(setup, "board-grant"));
        var decision = new ResourceChangeDecisionRequest(
            request.Id,
            ResourceChangeDecisionKinds.Approve,
            "Approved.",
            "board-grant-decision");

        await service.DecideForInstallationAsync(
            setup.OrganizationId,
            setup.ManagerInstallationId,
            decision);
        await service.DecideForInstallationAsync(
            setup.OrganizationId,
            setup.ManagerInstallationId,
            decision);

        var grant = Assert.Single(await db.ScopedActionGrants.ToListAsync());
        Assert.Equal(setup.OrganizationId, grant.OrganizationId);
        Assert.Equal(GrantSubjectKind.AgentInstallation, grant.SubjectKind);
        Assert.Equal(setup.RequesterInstallationId, grant.SubjectId);
        Assert.Equal(WorkBoardActions.Create, grant.Action);
        Assert.Equal(GrantScopeKind.Organization, grant.ScopeKind);
        Assert.Null(grant.ScopeId);
        Assert.False(grant.CanDelegate);
        Assert.Equal(GrantSubjectKind.OrganizationUser, grant.GrantedBySubjectKind);
    }

    [Fact]
    public async Task ApprovedDecision_DoesNotGrantBoardCreation_WhenPackageDoesNotRequireCapability()
    {
        await using var db = CreateDb();
        var setup = SeedManagerConversation(db);
        SeedRequesterInstallation(db, setup, ["work.item.read"]);
        await db.SaveChangesAsync();
        var service = new ResourceChangeService(db, new TestAuditEventWriter());
        var request = await service.ProposeAsync(
            setup.OrganizationId,
            setup.RequesterInstallationId,
            Proposal(setup, "no-board-grant"));

        await service.DecideForInstallationAsync(
            setup.OrganizationId,
            setup.ManagerInstallationId,
            new ResourceChangeDecisionRequest(
                request.Id,
                ResourceChangeDecisionKinds.Approve,
                "Approved.",
                "no-board-grant-decision"));

        Assert.Empty(await db.ScopedActionGrants.ToListAsync());
    }

    private static ResourceChangeProposalRequest Proposal(Setup setup, string key) =>
        new(
            setup.ConversationId,
            setup.TurnId,
            "Ship a measurable first product outcome",
            "The smallest complete cross-functional team needed for the approved product goal.",
            3,
            [
                new ResourceChangeRole(
                    "product-design",
                    "Product",
                    "Product Designer",
                    "Own customer research and interaction design.",
                    1,
                    1,
                    "Now",
                    ["customer-research", "interaction-design"],
                    false,
                    setup.RequesterId,
                    null),
                new ResourceChangeRole(
                    "quality",
                    "Product",
                    "Quality Engineer",
                    "Own independent product quality and release evidence.",
                    1,
                    2,
                    "Next",
                    ["quality-engineering"],
                    false,
                    setup.RequesterId,
                    null)
            ],
            ["The initial customer segment is known."],
            ["No approved workforce budget is implied."],
            null,
            key);

    private static Setup SeedManagerConversation(CSweetDbContext db)
    {
        var organizationId = Guid.NewGuid();
        var requesterInstallationId = Guid.NewGuid();
        var managerInstallationId = Guid.NewGuid();
        var manager = User(organizationId, "Chief of Staff", EmployeeType.Agent);
        manager.AgentInstallationId = managerInstallationId;
        var requester = User(organizationId, "Product Manager", EmployeeType.Agent);
        requester.AgentInstallationId = requesterInstallationId;
        requester.ReportsToOrganizationUserId = manager.Id;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Kind = ConversationKind.DirectHumanAgent,
            IsPrivate = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Participants =
            [
                new ConversationParticipant
                {
                    Id = Guid.NewGuid(), OrganizationUserId = manager.Id, JoinedAt = DateTimeOffset.UtcNow
                },
                new ConversationParticipant
                {
                    Id = Guid.NewGuid(), OrganizationUserId = requester.Id, JoinedAt = DateTimeOffset.UtcNow
                }
            ]
        };
        var userMessage = new ConversationMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            Sequence = 1,
            Role = ConversationRole.User,
            Content = "Prepare the complete initial team proposal.",
            SenderOrganizationUserId = manager.Id,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var turn = new ChatTurn
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ConversationId = conversation.Id,
            TargetAgentOrganizationUserId = requester.Id,
            UserMessageId = userMessage.Id,
            UserMessage = userMessage,
            Status = ChatTurnStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.AddRange(manager, requester, conversation, userMessage, turn);
        return new(
            organizationId,
            requester.Id,
            requesterInstallationId,
            managerInstallationId,
            conversation.Id,
            turn.Id,
            userMessage);
    }

    private static void SeedRequesterInstallation(
        CSweetDbContext db,
        Setup setup,
        IReadOnlyList<string> requiredCapabilities)
    {
        var now = DateTimeOffset.UtcNow;
        db.AgentInstallations.Add(new AgentInstallation
        {
            Id = setup.RequesterInstallationId,
            InstallationKey = Guid.NewGuid(),
            PackageVersionId = Guid.NewGuid(),
            BusinessId = setup.OrganizationId.ToString("D"),
            Scope = PluginInstallationScope.Organization,
            IsEnabled = true,
            RevisionStatus = PluginRevisionStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.AgentInstallationGrants.Add(new AgentInstallationGrant
        {
            Id = Guid.NewGuid(),
            AgentInstallationId = setup.RequesterInstallationId,
            RequiredCapabilitiesJson = System.Text.Json.JsonSerializer.Serialize(requiredCapabilities),
            ApprovedAt = now
        });
    }

    private static OrganizationUser User(Guid organizationId, string name, EmployeeType type) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        DisplayName = name,
        EmployeeType = type,
        PermissionLevel = OrganizationPermissionLevel.Manager,
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static CSweetDbContext CreateDb() => new(
        new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed record Setup(
        Guid OrganizationId,
        Guid RequesterId,
        Guid RequesterInstallationId,
        Guid ManagerInstallationId,
        Guid ConversationId,
        Guid TurnId,
        ConversationMessage UserMessage);
}
