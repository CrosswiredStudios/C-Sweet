using System.Reflection;
using System.Text.Json.Nodes;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.Communications;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed partial class ExecutiveDecisionServiceTests
{
    [Theory]
    [InlineData(false, false, "current")]
    [InlineData(true, false, "current")]
    [InlineData(false, true, "current")]
    [InlineData(true, true, "current")]
    [InlineData(false, false, "legacy-array")]
    [InlineData(true, false, "missing-link")]
    public async Task StandaloneQuestionProjectsIntoOverviewAndClearsAfterAnswer(
        bool openEnded, bool messageAttached, string storage)
    {
        await using var db = CreateDb();
        var setup = await SeedAsync(db);
        var decisions = new ExecutiveDecisionService(db, new ChatTurnService(db));
        var decision = await decisions.CreateAsync(new(setup.OrganizationId, setup.ConversationId,
            messageAttached ? null : setup.TurnId, messageAttached ? setup.AssistantMessageId : null,
            setup.InstallationId, "How should hiring approvals be handled?",
            openEnded ? [] : [new("review", "Review each hire", null), new("automatic", "Hire automatically", null)],
            openEnded ? "" : "review", "overview-question"));

        var row = await db.ExecutiveDecisions.SingleAsync(x => x.Id == decision.Id);
        var saved = JsonNode.Parse(row.OptionsJson)!.AsObject();
        Assert.True(saved.ContainsKey("workstreamDecisionId"));
        Assert.Null(saved["workstreamDecisionId"]); // Actual persisted standalone-question shape.
        if (storage == "legacy-array") row.OptionsJson = saved["options"]!.ToJsonString();
        if (storage == "missing-link") { saved.Remove("workstreamDecisionId"); row.OptionsJson = saved.ToJsonString(); }
        await db.SaveChangesAsync();

        var todos = DispatchProxy.Create<IPersonalTodoService, WorkIssuePostgresTests.TestProxy>();
        ((WorkIssuePostgresTests.TestProxy)(object)todos).InvokeMethod = (method, _) => method == "ListAsync"
            ? Task.FromResult(new Wire.PersonalTodoDirectory([])) : throw new NotSupportedException(method);
        var reader = new OverviewIssueReader(db, todos, new(db, todos, TimeProvider.System), decisions,
            new(db, new(db, TimeProvider.System), TimeProvider.System),
            new(db, new ScopedActionAuthorizationService(db), TimeProvider.System));

        var directory = await reader.ReadAsync(setup.OrganizationId, new(setup.OwnerId, null), false, default);
        var question = Assert.Single(Assert.Single(directory.UserActions).Questions);
        Assert.Equal(decision.Id, question.Decision.Id);
        Assert.Equal(decision.Prompt, question.Decision.Prompt);
        Assert.Equal(openEnded ? 0 : 2, question.Decision.Options.Count);
        Assert.Contains(await db.ApplicationRealtimeOutbox.ToListAsync(), x => x.EventType == CommunicationEvents.DecisionUpdated);

        var answer = await decisions.AnswerAsync(setup.OrganizationId, setup.ConversationId, decision.Id, setup.OwnerId,
            new(openEnded ? null : "review", openEnded ? "Review each hire" : null, "overview-answer"));
        Assert.True(answer.Succeeded);
        Assert.Empty((await reader.ReadAsync(setup.OrganizationId, new(setup.OwnerId, null), false, default)).UserActions);
    }
}
