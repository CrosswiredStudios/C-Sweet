using System.Reflection;
using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class ExecutiveDecisionServiceTests
{
    [Theory]
    [InlineData("How should we cover the missing work?", "Request staffing options", "The plan stays the same.",
        "Hiring would need separate approval.", "Does this hire anyone?")]
    [InlineData("How much automatic hiring authority should I have?", "Broad delegation",
        "Hire my own leadership recommendations and fill approved team roles. Install eligible packages and grant their requested permissions.",
        null, "Explain this option")]
    public async Task FollowUpQueuesForTheReporterOnceAndLeavesTheDecisionPending(string prompt, string label,
        string description, string? impact, string questionText)
    {
        await using var db = CreateDb();
        var setup = await SeedAsync(db);
        var decisions = new ExecutiveDecisionService(db, new ChatTurnService(db));
        var decision = await decisions.CreateAsync(new(setup.OrganizationId, setup.ConversationId, setup.TurnId, null,
            setup.InstallationId, prompt,
            [new("staff", label, description, impact),
             new("change", "Change the plan", "Propose a smaller release.")], "staff", "staffing"));
        var service = new DecisionFollowUpService(db, new ChatTurnService(db), decisions);
        var question = new DecisionQuestionRequest(questionText, "staff") { RequestId = Guid.NewGuid() };
        Task<Guid?> Ask(Guid user, Guid chat, DecisionQuestionRequest request) =>
            service.AskAsync(setup.OrganizationId, chat, decision.Id, user, request, default);
        Assert.Null(await Ask(Guid.NewGuid(), setup.ConversationId, question));
        Assert.Null(await Ask(setup.OwnerId, Guid.NewGuid(), question));
        Assert.Null(await Ask(setup.OwnerId, setup.ConversationId, question with { OptionId = "missing" }));
        var id = Assert.IsType<Guid>(await Ask(setup.OwnerId, setup.ConversationId, question));
        Assert.Equal(id, await Ask(setup.OwnerId, setup.ConversationId, question));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Ask(setup.OwnerId, setup.ConversationId, question with { Question = "Different question" }));
        Assert.Null(await service.HistoryAsync(setup.OrganizationId, setup.ConversationId, decision.Id, Guid.NewGuid(), default));
        var visible = Assert.Single((await service.HistoryAsync(setup.OrganizationId, setup.ConversationId, decision.Id, setup.OwnerId, default))!);
        Assert.Equal(questionText, visible.Question);
        Assert.Equal(label, visible.OptionLabel);
        Assert.Equal("Queued", visible.Status);
        var agent = await db.CoreOrganizationUsers.SingleAsync(p => p.AgentInstallationId == setup.InstallationId);
        Assert.Equal(agent.Id, (await db.ChatTurns.SingleAsync(t => t.Id == id)).TargetAgentOrganizationUserId);
        var row = await db.ExecutiveDecisions.AsNoTracking().SingleAsync();
        Assert.Equal(ExecutiveDecisionStatus.Pending, row.Status);
        Assert.Null(row.SelectedOptionId);
        Assert.Null(row.FreeTextAnswer);
        Assert.Null(row.AnsweredAt);
        Assert.Equal(2, await db.ChatTurns.CountAsync());
        Assert.Single(await decisions.ListPendingForUserAsync(setup.OrganizationId, setup.OwnerId));
        await new ChatTurnService(db).CancelAsync(setup.OrganizationId, id);
        Assert.True((await decisions.AnswerAsync(setup.OrganizationId, setup.ConversationId, decision.Id, setup.OwnerId,
            new("staff", null, "explicit-answer"))).Succeeded);
        Assert.Null(await Ask(setup.OwnerId, setup.ConversationId, question with { RequestId = Guid.NewGuid() }));
    }

    private static T Proxy<T>(Func<string, object?[]?, object> invoke) where T : class
    {
        var result = DispatchProxy.Create<T, WorkIssuePostgresTests.TestProxy>();
        ((WorkIssuePostgresTests.TestProxy)(object)result).InvokeMethod = invoke;
        return result;
    }
}
