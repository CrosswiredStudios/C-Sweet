using CSweet.Application.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class ExecutiveDecisionServiceTests
{
    [Fact]
    public async Task QuestionInboxOnlyIncludesActivePersonalPendingQuestions()
    {
        await using var db = CreateDb();
        var first = await SeedAsync(db);
        var other = await SeedAsync(db);
        var service = new ExecutiveDecisionService(db, new ChatTurnService(db));
        foreach (var setup in new[] { first, other })
            await service.CreateAsync(new CreateExecutiveDecisionCommand(setup.OrganizationId, setup.ConversationId,
                setup.TurnId, null, setup.InstallationId, "Choose direction", [new("a", "Proceed", null), new("b", "Wait", null)], "a", "inbox"));
        var question = Assert.Single(await service.ListPendingForUserAsync(first.OrganizationId, first.OwnerId));
        Assert.Equal(first.ConversationId, question.ConversationId);
        Assert.Equal("Chief", question.AgentName);
        Assert.Empty(await service.ListPendingForUserAsync(first.OrganizationId, other.OwnerId));
        var member = await db.ConversationParticipants.SingleAsync(x => x.ConversationId == first.ConversationId && x.OrganizationUserId == first.OwnerId);
        member.LeftAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        Assert.Empty(await service.ListPendingForUserAsync(first.OrganizationId, first.OwnerId));
        member.LeftAt = null;
        var chat = await db.CoreConversations.SingleAsync(x => x.Id == first.ConversationId);
        chat.ArchivedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        Assert.Empty(await service.ListPendingForUserAsync(first.OrganizationId, first.OwnerId));
        chat.ArchivedAt = null;
        await db.SaveChangesAsync();
        var answer = await service.AnswerAsync(first.OrganizationId, first.ConversationId, question.Decision.Id, first.OwnerId,
            new("a", null, "inbox-answer"));
        Assert.True(answer.Succeeded, answer.Message);
        Assert.Empty(await service.ListPendingForUserAsync(first.OrganizationId, first.OwnerId));
        Assert.Single(await service.ListPendingForUserAsync(other.OrganizationId, other.OwnerId));
    }
}
