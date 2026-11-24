using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.Testing;
using CSweet.AgentHost.Broker;
using CSweet.AI.Providers;
using CSweet.Application.Setup;
using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;

namespace CSweet.UnitTests;

public sealed partial class ExecutiveDecisionServiceTests
{
    [Theory, Trait("Category", "AgentHarness")]
    [InlineData("independent.research", "Avery", "Research Director", "research brief")]
    [InlineData("studio.production", "Gabriel", "Producer", "release plan")]
    public async Task AuthoredQuestionStaysVerbatimAndOnlyTheSelectedOptionGetsGeneratedDetails(string package, string name, string role, string work)
    {
        await using var db = CreateDb();
        var setup = await SeedAsync(db);
        var employee = await db.CoreOrganizationUsers.SingleAsync(u => u.AgentInstallationId == setup.InstallationId);
        employee.DisplayName = name;
        employee.Role = new Role { Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, Name = role };
        db.CoreRoles.Add(employee.Role);
        await db.SaveChangesAsync();

        var decisions = new ExecutiveDecisionService(db, new ChatTurnService(db));
        var handler = new CommunicationHubCapabilityHandler(db, null!, decisions);
        var session = new AgentSession("test", package, setup.InstallationId.ToString(), setup.OrganizationId.ToString(),
            "test", "test", new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { CommunicationHubCapabilities.AskUser }, 1));
        var runtime = new AgentTestRuntime().RegisterCapability<AskUserRequest, UserQuestionResponse>(CommunicationHubCapabilities.AskUser,
            async (request, ct) =>
            {
                var command = new RequestCapability { RequestId = Guid.NewGuid().ToString(), Capability = CommunicationHubCapabilities.AskUser,
                    Payload = JsonPayload.From(request) };
                await foreach (var reply in handler.HandleAsync(session, command, ct))
                {
                    Assert.True(reply.Succeeded, reply.Error);
                    return JsonSerializer.Deserialize<UserQuestionResponse>(reply.Payload.Span, JsonSerializerOptions.Web)!;
                }
                throw new InvalidOperationException("Missing broker response.");
            });
        var host = new AgentTestHost(new AskingAgent(package), runtime, businessId: session.BusinessId, installationId: session.InstallationId);
        var request = new AskUserRequest(setup.ConversationId, setup.TurnId, $"Would you like to review my {work}?",
            [new("review", "Review first", "Hold for review.") { Impact = "Owner approval is required." }, new("later", "Defer")], "review", "review");
        var delivery = await host.DeliverEventAsync("test.user-question", request);
        Assert.True(delivery.Succeeded, delivery.Error);
        var original = Assert.Single(await decisions.ListPendingForUserAsync(setup.OrganizationId, setup.OwnerId)).Decision;

        var calls = 0;
        var client = Proxy<IChatClient>((method, args) =>
        {
            if (method is "Dispose" or "GetService") return null!;
            var messages = ((IEnumerable<ChatMessage>)args![0]!).ToArray();
            Assert.Contains(CSweet.Application.Communications.DecisionWriting.Voice, messages[0].Text);
            var facts = JsonSerializer.Deserialize<JsonElement>(messages[1].Text);
            Assert.Equal(name, facts.GetProperty("speaker").GetProperty("Name").GetString());
            Assert.Equal(role, facts.GetProperty("speaker").GetProperty("Role").GetString());
            Assert.Equal(request.Prompt, facts.GetProperty("Prompt").GetString());
            Assert.Equal("Review first", facts.GetProperty("option").GetProperty("Label").GetString());
            Assert.Equal("Owner approval is required.", facts.GetProperty("option").GetProperty("Impact").GetString());
            Assert.DoesNotContain("Defer", messages[1].Text);
            calls++;
            const string text = """{"text":"I would wait for your review.","impact":"You would approve the next step."}""";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        });
        var provider = Guid.NewGuid();
        var config = Proxy<IAgentInstallationConfigurationService>((_, args) =>
        {
            Assert.Equal(setup.InstallationId, args![0]);
            return Task.FromResult<AgentInstallationConfigurationSnapshot?>(new(setup.InstallationId, "v1",
                new Dictionary<string, JsonElement> { ["llmProviderId"] = JsonSerializer.SerializeToElement(provider) }, default, default));
        });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new DecisionExplanationService(db, decisions, config, new ConversationService(db),
            Proxy<ILlmProviderFactory>((_, _) => Task.FromResult(client)), cache);
        Task<DecisionWordingResponse?> Wording(Guid user, string? option) =>
            service.GetWordingAsync(setup.OrganizationId, setup.ConversationId, original.Id, user, new(option), default);
        Assert.Equal(request.Prompt, (await Wording(setup.OwnerId, null))?.Text);
        Assert.Equal(0, calls);
        Assert.Equal("You would approve the next step.", (await Wording(setup.OwnerId, "review"))?.Impact);
        Assert.Equal("I would wait for your review.", (await Wording(setup.OwnerId, "review"))?.Text);
        Assert.Null(await Wording(Guid.NewGuid(), "review"));
        Assert.Null(await Wording(setup.OwnerId, "unknown"));
        Assert.Equal(1, calls);
        var pending = Assert.Single(await decisions.ListPendingForUserAsync(setup.OrganizationId, setup.OwnerId)).Decision;
        Assert.Equal(request.Prompt, pending.Prompt);
        Assert.Equal("Owner approval is required.", pending.Options[0].Impact);
        var confirmed = await decisions.AnswerAsync(setup.OrganizationId, setup.ConversationId, original.Id, setup.OwnerId, new("review", null, "confirm"));
        Assert.True(confirmed.Succeeded);
        Assert.Equal("review", confirmed.Decision?.SelectedOptionId);
        Assert.Null(await Wording(setup.OwnerId, "review"));
    }

    private sealed class AskingAgent(string package) : CSweetAgentBase
    {
        public override string AgentId => package;
        public override string Version => "1.0.0";
        public override async Task HandleEventAsync(AgentEventEnvelope envelope, AgentRuntimeContext context, CancellationToken token) =>
            await context.Platform.AskUserAsync(envelope.Data.Deserialize<AskUserRequest>(JsonSerializerOptions.Web)!, token);
    }
}
