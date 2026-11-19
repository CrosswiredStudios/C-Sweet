using System.Reflection;
using System.Text.Json;
using CSweet.AI.Providers;
using CSweet.Application.Setup;
using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace CSweet.UnitTests;

public sealed partial class ExecutiveDecisionServiceTests
{
    [Fact]
    public async Task FollowUpExplainsOnlyTheVisibleDecisionAndLeavesItPending()
    {
        await using var db = CreateDb();
        var setup = await SeedAsync(db);
        var decisions = new ExecutiveDecisionService(db, new ChatTurnService(db));
        var decision = await decisions.CreateAsync(new(setup.OrganizationId, setup.ConversationId, setup.TurnId, null,
            setup.InstallationId, "How should we cover the missing work?",
            [new("staff", "Request staffing options", "The plan stays the same.", "Hiring would need separate approval."),
             new("change", "Change the plan", "Propose a smaller release.")], "staff", "staffing"));
        var config = Proxy<IAgentInstallationConfigurationService>((_, _) =>
            Task.FromResult<AgentInstallationConfigurationSnapshot?>(new(setup.InstallationId, "v1",
                new Dictionary<string, JsonElement> { ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()) },
                default, default)));
        int calls = 0;
        var client = Proxy<IChatClient>((method, args) =>
        {
            if (method is "Dispose" or "GetService") return null!;
            Assert.Equal("GetResponseAsync", method);
            Assert.Null(((ChatOptions?)args![1])?.Tools);
            var input = ((IEnumerable<ChatMessage>)args[0]!).Last().Text;
            Assert.Contains("Hiring would need separate approval.", input);
            Assert.DoesNotContain("Propose a smaller release", input);
            Assert.Contains("Does this hire anyone?", input);
            calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hiring needs a separate approval.")));
        });
        var service = new DecisionExplanationService(db, decisions, config, new ConversationService(db),
            Proxy<ILlmProviderFactory>((_, _) => Task.FromResult(client)));
        var question = new DecisionQuestionRequest("Does this hire anyone?", "staff");
        async Task<DecisionExplanationResponse?> Ask(Guid user, Guid chat, DecisionQuestionRequest request) =>
            await service.ExplainAsync(setup.OrganizationId, chat, decision.Id, user, request, default);
        Assert.Null(await Ask(Guid.NewGuid(), setup.ConversationId, question));
        Assert.Null(await Ask(setup.OwnerId, Guid.NewGuid(), question));
        Assert.Null(await Ask(setup.OwnerId, setup.ConversationId, question with { OptionId = "missing" }));
        Assert.Equal("Hiring needs a separate approval.", (await Ask(setup.OwnerId, setup.ConversationId, question))?.Explanation);
        var row = await db.ExecutiveDecisions.AsNoTracking().SingleAsync();
        Assert.Equal(ExecutiveDecisionStatus.Pending, row.Status);
        Assert.Null(row.SelectedOptionId);
        Assert.Null(row.FreeTextAnswer);
        Assert.Null(row.AnsweredAt);
        Assert.Single(await db.ChatTurns.ToListAsync());
        Assert.Single(await decisions.ListPendingForUserAsync(setup.OrganizationId, setup.OwnerId));
        Assert.True((await decisions.AnswerAsync(setup.OrganizationId, setup.ConversationId, decision.Id, setup.OwnerId,
            new("staff", null, "explicit-answer"))).Succeeded);
        Assert.Null(await Ask(setup.OwnerId, setup.ConversationId, question));
        Assert.Equal(1, calls);
    }

    private static T Proxy<T>(Func<string, object?[]?, object> invoke) where T : class
    {
        var result = DispatchProxy.Create<T, WorkIssuePostgresTests.TestProxy>();
        ((WorkIssuePostgresTests.TestProxy)(object)result).InvokeMethod = invoke;
        return result;
    }
}
