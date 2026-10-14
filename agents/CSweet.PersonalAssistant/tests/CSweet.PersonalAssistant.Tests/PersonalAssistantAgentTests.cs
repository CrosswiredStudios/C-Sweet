using CSweet.Agent.SDK;

namespace CSweet.PersonalAssistant.Tests;

public sealed class PersonalAssistantAgentTests
{
    [Fact]
    public async Task StandardSdkConfigurationSelectsProviderModelAndBudget()
    {
        var reasoner = new FakeReasoner(); var agent = new PersonalAssistantAgent(reasoner);
        var runtime = new AgentTestRuntime(); var provider = Guid.NewGuid();
        var configured = await runtime.ExecuteCapabilityAsync(agent, AgentConfigurationCapabilities.Update,
            new { settings = new { llmProviderId = provider.ToString(), llmModel = "test-model", maxContextWindowTokens = 65536, maxOutputTokens = 8192 } });
        Assert.True(configured.Succeeded, configured.Error);
        var result = await runtime.ExecuteCapabilityAsync(agent, PersonalAssistantAgent.ConverseCapability,
            new ConversationRequest([new("user", "Help me onboard my game.")], null));
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("Let's prepare your brief.", result.Value!.Value.GetProperty("message").GetString());
        Assert.Equal(provider, reasoner.Selection!.ProviderProfileId);
        Assert.Equal("test-model", reasoner.Selection.Model);
        Assert.Equal(8192, reasoner.OutputTokens);
        Assert.Single(runtime.Progress);
    }

    [Fact]
    public async Task MissingConfigurationAndUnsupportedCapabilityFailSafely()
    {
        var agent = new PersonalAssistantAgent(new FakeReasoner()); var runtime = new AgentTestRuntime();
        Assert.False((await runtime.ExecuteCapabilityAsync(agent, PersonalAssistantAgent.ConverseCapability, new { messages = new[] { new { role = "user", content = "Hi" } } })).Succeeded);
        var unsupported = await runtime.ExecuteCapabilityAsync(agent, "business.manage.v1", new { });
        Assert.False(unsupported.Succeeded); Assert.Contains("not supported", unsupported.Error);
    }

    [Fact]
    public async Task OutputBudgetMustFitContextWindow()
    {
        var result = await new AgentTestRuntime().ExecuteCapabilityAsync(new PersonalAssistantAgent(new FakeReasoner()),
            AgentConfigurationCapabilities.Update, new { settings = new { llmProviderId = Guid.NewGuid().ToString(), llmModel = "model", maxContextWindowTokens = 8192, maxOutputTokens = 8192 } });
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task NullMessagesFailWithoutCallingModel()
    {
        var reasoner = new FakeReasoner(); var agent = new PersonalAssistantAgent(reasoner); var runtime = new AgentTestRuntime();
        await runtime.ExecuteCapabilityAsync(agent, AgentConfigurationCapabilities.Update, new { settings = new { llmProviderId = Guid.NewGuid().ToString(), llmModel = "model" } });
        var result = await runtime.ExecuteCapabilityAsync(agent, PersonalAssistantAgent.ConverseCapability, new { messages = new object?[] { null } });
        Assert.False(result.Succeeded); Assert.Null(reasoner.Selection);
    }

    [Fact]
    public async Task UngrantedBusinessAccessIsDenied()
    {
        var error = await Assert.ThrowsAsync<PlatformCapabilityException>(() => new AgentTestRuntime().CreateContext().Platform.ReadBusinessProfileAsync());
        Assert.Equal(PlatformCapabilityErrorCode.Denied, error.Code);
    }

    private sealed class FakeReasoner : IPersonalAssistantReasoner
    {
        public AgentLlmSelection? Selection; public int OutputTokens;
        public Task<string> ConverseAsync(ConversationRequest request, AgentRuntimeContext context, AgentLlmSelection selection, int outputTokens, CancellationToken token)
        { Selection = selection; OutputTokens = outputTokens; return Task.FromResult("Let's prepare your brief."); }
        public Task<CompanyBrief> AnalyzeAsync(RepositoryAnalysisRequest request, AgentRuntimeContext context, AgentLlmSelection selection, int outputTokens, CancellationToken token) => throw new NotSupportedException();
    }
}
