using System.Text.Json;
using CSweet.Application.Communications;
using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed partial class ExecutiveDecisionServiceTests
{
    [Fact]
    public void PendingCardConstraintExcludesLinkedProjectDecisions()
    {
        using var db = CreateDb();
        var index = db.Model.FindEntityType(typeof(ExecutiveDecision))!.GetIndexes().Single(x =>
            x.Properties.Select(p => p.Name).SequenceEqual(new[] { "ConversationId", "RequestingInstallationId", "Status" }));
        var migration = new CSweet.Infrastructure.Persistence.Migrations.AllowIndependentProjectDecisionCards();
        var created = Assert.Single(migration.UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.CreateIndexOperation>());
        Assert.True(index.IsUnique);
        Assert.Equal(index.GetFilter(), created.Filter);
        Assert.Contains("workstreamDecisionId", created.Filter);
        Assert.Contains("IS NULL", created.Filter);
    }

    [Fact]
    public async Task ProjectReviewReplayReusesDirectChatAndReturnsAnActionableMessage()
    {
        await using var db = CreateDb();
        var setup = await SeedAsync(db);
        var agent = await db.CoreOrganizationUsers.SingleAsync(x => x.AgentInstallationId == setup.InstallationId);
        var stream = new Workstream { Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, Name = "Game", ProfileKey = "game" };
        var source = new WorkstreamDecisionRecord {
            Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, WorkstreamId = stream.Id,
            RequestedByInstallationId = setup.InstallationId, RequestedByOrganizationUserId = agent.Id,
            Summary = "Choose the asset strategy", Status = "Pending", Revision = 1,
            OptionsJson = JsonSerializer.Serialize(new[] { new W.DecisionOption("procedural", "Procedural assets", "Generate assets"),
                new W.DecisionOption("licensed", "Licensed assets", "Use licensed assets") }), RecommendedOptionId = "procedural"
        };
        db.Workstreams.Add(stream);
        db.WorkstreamDecisions.Add(source);
        await db.SaveChangesAsync();
        var turns = new ChatTurnService(db);
        var decisions = new ExecutiveDecisionService(db, turns);
        var hub = new CommunicationHubService(db, new TestAuditEventWriter(), turns, decisions);
        await CSweet.AgentHost.Broker.WorkstreamDecisionChatReview.PresentAsync(db, hub, decisions, source, default);
        await CSweet.AgentHost.Broker.WorkstreamDecisionChatReview.PresentAsync(db, hub, decisions, source, default);
        Assert.Single(await db.CoreConversations.ToListAsync());
        var card = Assert.Single(await db.ExecutiveDecisions.ToListAsync());
        var messages = await hub.ListMessagesAsync(setup.OrganizationId, setup.ConversationId, setup.OwnerId);
        var message = Assert.Single(messages!, x => x.Decision?.Id == card.Id);
        Assert.False(message.Decision!.AllowFreeText);
        var answer = await decisions.AnswerAsync(setup.OrganizationId, setup.ConversationId, card.Id, setup.OwnerId,
            new("procedural", null, "choose-assets"));
        Assert.True(answer.Succeeded, answer.Message);
        Assert.Equal("Decided", source.Status);
        Assert.Equal("procedural", source.SelectedOptionId);
        Assert.NotNull(answer.Turn);
    }

    [Fact]
    public async Task ProjectCardsRemainPendingIndependentlyAndAnswerTheAuthoritativeDecision()
    {
        await using var db = CreateDb();
        var setup = await SeedAsync(db);
        var stream = new Workstream { Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, Name = "Game", ProfileKey = "game" };
        db.Workstreams.Add(stream);
        var sources = Enumerable.Range(0, 2).Select(i => new WorkstreamDecisionRecord {
            Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, WorkstreamId = stream.Id,
            RequestedByInstallationId = setup.InstallationId, Summary = $"Direction {i}", Status = "Pending",
            OptionsJson = JsonSerializer.Serialize(new[] { new W.DecisionOption("provide-direction", "Provide direction", "Enter direction"),
                new W.DecisionOption("continue-current-plan", "Continue current plan", "Keep the accepted scope") }),
            RecommendedOptionId = "provide-direction", Revision = 1
        }).ToArray();
        db.WorkstreamDecisions.AddRange(sources);
        await db.SaveChangesAsync();
        var service = new ExecutiveDecisionService(db, new ChatTurnService(db));
        var cards = new List<ExecutiveDecisionCardResponse>();
        foreach (var source in sources)
            cards.Add(await service.CreateAsync(new CreateExecutiveDecisionCommand(setup.OrganizationId, setup.ConversationId,
                setup.TurnId, null, setup.InstallationId, source.Summary,
                [new("provide-direction", "Provide direction", "Enter direction"), new("continue-current-plan", "Continue current plan", "Keep the accepted scope")],
                "provide-direction", source.Id.ToString()) { WorkstreamDecisionId = source.Id }));
        Assert.Equal(2, await db.ExecutiveDecisions.CountAsync(x => x.Status == ExecutiveDecisionStatus.Pending));
        Assert.Equal(2, (await service.ListPendingForUserAsync(setup.OrganizationId, setup.OwnerId)).Count);
        var invalid = await service.AnswerAsync(setup.OrganizationId, setup.ConversationId, cards[0].Id, setup.OwnerId,
            new("provide-direction", null, "empty"));
        Assert.False(invalid.Succeeded);
        Assert.Equal("Pending", sources[0].Status);
        var answered = await service.AnswerAsync(setup.OrganizationId, setup.ConversationId, cards[0].Id, setup.OwnerId,
            new(null, "Target desktop browsers at 60 FPS.", "answer"));
        Assert.True(answered.Succeeded, answered.Message);
        Assert.Equal("Decided", sources[0].Status);
        Assert.Equal("provide-direction", sources[0].SelectedOptionId);
        Assert.Equal("Target desktop browsers at 60 FPS.", sources[0].Rationale);
        Assert.Equal(setup.OwnerId, sources[0].DecidedByOrganizationUserId);
        Assert.Equal("Pending", sources[1].Status);
        Assert.Single(await service.ListPendingForUserAsync(setup.OrganizationId, setup.OwnerId));
        Assert.Single(await db.AgentPlatformEventOutbox.Where(x => x.EventType == W.WorkstreamEventNames.DecisionDecidedV1).ToListAsync());
        var replay = await service.AnswerAsync(setup.OrganizationId, setup.ConversationId, cards[0].Id, setup.OwnerId,
            new(null, "ignored", "answer"));
        Assert.True(replay.Succeeded);
        Assert.Equal(2, sources[0].Revision);
        var owner = await db.CoreOrganizationUsers.SingleAsync(x => x.Id == setup.OwnerId);
        owner.PermissionLevel = OrganizationPermissionLevel.Manager;
        await db.SaveChangesAsync();
        Assert.Empty(await service.ListPendingForUserAsync(setup.OrganizationId, setup.OwnerId));
        var denied = await service.AnswerAsync(setup.OrganizationId, setup.ConversationId, cards[1].Id, setup.OwnerId,
            new("continue-current-plan", null, "denied"));
        Assert.False(denied.Succeeded);
        owner.PermissionLevel = OrganizationPermissionLevel.Owner;
        sources[1].Revision++;
        await db.SaveChangesAsync();
        Assert.Empty(await service.ListPendingForUserAsync(setup.OrganizationId, setup.OwnerId));
        var stale = await service.AnswerAsync(setup.OrganizationId, setup.ConversationId, cards[1].Id, setup.OwnerId,
            new("continue-current-plan", null, "stale"));
        Assert.False(stale.Succeeded);
        Assert.Equal("Pending", sources[1].Status);
    }
}
