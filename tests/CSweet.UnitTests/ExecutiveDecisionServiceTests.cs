using CSweet.Application.Communications;
using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ExecutiveDecisionServiceTests
{
    [Fact]
    public async Task NewDecision_SupersedesPendingDecisionInSameAgentConversation()
    {
        await using var db = CreateDb();
        var setup = await SeedAsync(db);
        var audit = new TestAuditEventWriter();
        var service = new ExecutiveDecisionService(db, new ChatTurnService(db), audit);
        var options = new[] { new CreateExecutiveDecisionOption("a", "Proceed", null), new CreateExecutiveDecisionOption("b", "Wait", null) };

        var first = await service.CreateAsync(new(setup.OrganizationId, setup.ConversationId, setup.TurnId,
            setup.InstallationId, "Proceed now?", options, "a", "decision-1"));
        var second = await service.CreateAsync(new(setup.OrganizationId, setup.ConversationId, setup.TurnId,
            setup.InstallationId, "Choose the launch path", options, "a", "decision-2"));

        Assert.Equal("Superseded", (await db.ExecutiveDecisions.SingleAsync(x => x.Id == first.Id)).Status.ToString());
        Assert.Equal("Pending", second.Status);
        Assert.Single(await db.ExecutiveDecisions.Where(x => x.Status == ExecutiveDecisionStatus.Pending).ToListAsync());
    }

    [Fact]
    public async Task SomethingElseAnswer_IsImmutableIdempotentAndStartsNextTurn()
    {
        await using var db = CreateDb();
        var setup = await SeedAsync(db);
        var audit = new TestAuditEventWriter();
        var service = new ExecutiveDecisionService(db, new ChatTurnService(db), audit);
        var decision = await service.CreateAsync(new(setup.OrganizationId, setup.ConversationId, setup.TurnId,
            setup.InstallationId, "Choose", [new("a", "Proceed", null), new("b", "Wait", null)], "a", "decision"));

        var first = await service.AnswerAsync(setup.OrganizationId, setup.ConversationId, decision.Id, setup.OwnerId,
            new AnswerExecutiveDecisionRequest(null, "Run a smaller pilot", "answer-1"));
        var replay = await service.AnswerAsync(setup.OrganizationId, setup.ConversationId, decision.Id, setup.OwnerId,
            new AnswerExecutiveDecisionRequest(null, "ignored replay text", "answer-1"));

        Assert.True(first.Succeeded);
        Assert.Equal(first.Turn?.Id, replay.Turn?.Id);
        Assert.Equal("Run a smaller pilot", replay.Decision?.FreeTextAnswer);
        Assert.Single(await db.ChatTurns.Where(x => x.Id != setup.TurnId).ToListAsync());
        Assert.Single(audit.Events, x => x.EventType == "communication.user-input.requested");
        Assert.Single(audit.Events, x => x.EventType == "communication.user-input.responded");
        Assert.DoesNotContain("Run a smaller pilot", string.Join("", audit.Events.Select(x => x.MetadataJson)));
    }

    private static async Task<Setup> SeedAsync(CSweetDbContext db)
    {
        var organizationId = Guid.NewGuid(); var installationId = Guid.NewGuid();
        var ownerId = Guid.NewGuid(); var agentId = Guid.NewGuid(); var conversationId = Guid.NewGuid();
        var turnId = Guid.NewGuid(); var messageId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        db.CoreOrganizations.Add(new Organization { Id = organizationId, Name = "Example", CreatedAt = now, UpdatedAt = now });
        db.CoreOrganizationUsers.AddRange(
            new OrganizationUser { Id = ownerId, OrganizationId = organizationId, DisplayName = "Owner", EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner, CreatedAt = now },
            new OrganizationUser { Id = agentId, OrganizationId = organizationId, AgentInstallationId = installationId, DisplayName = "Chief", EmployeeType = EmployeeType.Agent, PermissionLevel = OrganizationPermissionLevel.Manager, CreatedAt = now });
        db.CoreConversations.Add(new Conversation { Id = conversationId, OrganizationId = organizationId, AgentOrganizationUserId = agentId,
            InitiatedByOrganizationUserId = ownerId, Kind = ConversationKind.DirectHumanAgent, CreatedAt = now, UpdatedAt = now });
        db.ConversationParticipants.AddRange(
            new ConversationParticipant { Id = Guid.NewGuid(), ConversationId = conversationId, OrganizationUserId = ownerId, JoinedAt = now },
            new ConversationParticipant { Id = Guid.NewGuid(), ConversationId = conversationId, OrganizationUserId = agentId, JoinedAt = now });
        db.CoreConversationMessages.Add(new ConversationMessage { Id = messageId, ConversationId = conversationId, ChatTurnId = turnId,
            Role = ConversationRole.User, Content = "Help", CreatedAt = now, CorrelationId = Guid.NewGuid() });
        db.ChatTurns.Add(new ChatTurn { Id = turnId, OrganizationId = organizationId, ConversationId = conversationId,
            TargetAgentOrganizationUserId = agentId, UserMessageId = messageId, Status = ChatTurnStatus.Completed,
            CreatedAt = now, UpdatedAt = now, CompletedAt = now });
        await db.SaveChangesAsync();
        return new(organizationId, installationId, ownerId, conversationId, turnId);
    }

    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private sealed record Setup(Guid OrganizationId, Guid InstallationId, Guid OwnerId, Guid ConversationId, Guid TurnId);
}
