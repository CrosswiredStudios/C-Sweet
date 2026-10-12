using CSweet.Application.Communications;
using CSweet.Contracts.Communications;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class ExecutiveDecisionServiceTests
{
    [Fact]
    public async Task OpenEndedQuestion_IsDiscoverableLiveAndResumesOnceWithMultilineAnswer()
    {
        await using var db = CreateDb();
        var setup = await SeedAsync(db);
        var service = new ExecutiveDecisionService(db, new ChatTurnService(db));
        var command = new CreateExecutiveDecisionCommand(setup.OrganizationId, setup.ConversationId,
            setup.TurnId, null, setup.InstallationId, "What initial game direction should I build on?", [], "", "initial-direction");
        var card = await service.CreateAsync(command);
        Assert.Equal(card.Id, (await service.CreateAsync(command)).Id);
        Assert.Empty(card.Options);
        Assert.True(card.AllowFreeText);
        Assert.Equal(card.Id, (await service.ListForMessagesAsync(setup.OrganizationId, setup.ConversationId))[setup.TurnId].Id);
        Assert.Equal(card.Id, Assert.Single(await service.ListPendingForUserAsync(setup.OrganizationId, setup.OwnerId)).Decision.Id);
        Assert.Single(await db.ApplicationRealtimeOutbox.Where(x => x.EventType == CommunicationEvents.DecisionUpdated).ToListAsync());

        foreach (var invalid in new[] { new AnswerExecutiveDecisionRequest(null, " \n ", "blank"), new("made-up", null, "option") })
            Assert.False((await service.AnswerAsync(setup.OrganizationId, setup.ConversationId, card.Id, setup.OwnerId, invalid)).Succeeded);
        var answer = new AnswerExecutiveDecisionRequest(null, "Cozy exploration\nPC, no combat", "answer-direction");
        var first = await service.AnswerAsync(setup.OrganizationId, setup.ConversationId, card.Id, setup.OwnerId, answer);
        var retry = await service.AnswerAsync(setup.OrganizationId, setup.ConversationId, card.Id, setup.OwnerId, answer);
        Assert.True(first.Succeeded, first.Message);
        Assert.True(retry.Succeeded);
        Assert.Equal(first.Turn!.Id, retry.Turn!.Id);
        Assert.Equal(answer.SomethingElse, retry.Decision!.FreeTextAnswer);
        Assert.Empty(await service.ListPendingForUserAsync(setup.OrganizationId, setup.OwnerId));
        Assert.Single(await db.ChatTurns.Where(x => x.Id != setup.TurnId).ToListAsync());
        var turn = await db.ChatTurns.SingleAsync(x => x.Id == first.Turn.Id);
        Assert.Contains("Answer: Cozy exploration\nPC, no combat", (await db.CoreConversationMessages.SingleAsync(x => x.Id == turn.UserMessageId)).Content);
    }

    [Fact]
    public async Task OpenEndedQuestion_RejectsRecommendationAndTypedActions()
    {
        await using var db = CreateDb();
        var setup = await SeedAsync(db);
        var service = new ExecutiveDecisionService(db, new ChatTurnService(db));
        var command = new CreateExecutiveDecisionCommand(setup.OrganizationId, setup.ConversationId,
            setup.TurnId, null, setup.InstallationId, "Describe your direction", [], "", "invalid-direction");
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(command with { RecommendedOptionId = "a" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(command with { Options = [new("a", "Only one", null)] }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(command with { WorkstreamDecisionId = Guid.NewGuid() }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(command with { TaskMerge = new(Guid.NewGuid(), 1) }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(command with { ConfigurationChange = new("profile", "a", "b") }));
        Assert.Empty(db.ExecutiveDecisions);
    }
}
