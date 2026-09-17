using CSweet.AI.AgentFramework;
using CSweet.AI.Providers;
using CSweet.Application.Llm;
using CSweet.Contracts.Llm;
using CSweet.Domain.Setup;
using Microsoft.Extensions.AI;

namespace CSweet.IntegrationTests;

public class AgentRunnerIntegrationTests
{
    private readonly FakeLlmProviderFactory _providerFactory;
    private readonly List<AgentRunLog> _logsWritten;
    private readonly IAgentRunner _runner;

    public AgentRunnerIntegrationTests()
    {
        _providerFactory = new FakeLlmProviderFactory(new FakeChatClient("Test agent response content."));
        _logsWritten = new List<AgentRunLog>();

        var logWriter = new InMemoryAgentRunLogWriter(_logsWritten);
        _runner = new AgentFrameworkAgentRunner(_providerFactory, logWriter);
    }

    [Fact]
    public async Task RunAsync_CallsProviderFactoryAndReturnsResult()
    {
        var request = CreateRequest();
        var result = await _runner.RunAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal("Test agent response content.", result.Content);
        Assert.Null(result.FailureMessage);
    }

    [Fact]
    public async Task RunAsync_PersistsAgentRunLog()
    {
        var request = CreateRequest();
        await _runner.RunAsync(request);

        Assert.Single(_logsWritten);
        var log = _logsWritten[0];
        Assert.Equal("test-agent", log.AgentKey);
        Assert.Equal(request.ProviderProfileId, log.ProviderProfileId);
        Assert.Equal("Completed", log.Status);
        Assert.NotNull(log.PromptHash);
        Assert.True(log.DurationMs >= 0);
    }

    [Fact]
    public async Task RunAsync_PersistsTokenUsageWhenProviderReturnsUsage()
    {
        var runner = new AgentFrameworkAgentRunner(
            new FakeLlmProviderFactory(new UsageChatClient()),
            new InMemoryAgentRunLogWriter(_logsWritten));

        await runner.RunAsync(CreateRequest());

        var log = Assert.Single(_logsWritten);
        Assert.Equal(12, log.TokenInputCount);
        Assert.Equal(34, log.TokenOutputCount);
    }

    [Fact]
    public async Task RunAsync_Failure_CapturesErrorInLog()
    {
        var brokenFactory = new ThrowingLlmProviderFactory();
        var logWriter = new InMemoryAgentRunLogWriter(_logsWritten);
        var runner = new AgentFrameworkAgentRunner(brokenFactory, logWriter);

        var request = CreateRequest();
        var result = await runner.RunAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal("Simulated provider failure.", result.FailureMessage);

        Assert.Single(_logsWritten);
        var log = _logsWritten[0];
        Assert.Equal("Failed", log.Status);
        Assert.Equal("Simulated provider failure.", log.FailureMessage);
    }

    [Fact]
    public async Task RunAsync_DoesNotStoreFullPrompts()
    {
        var request = CreateRequest(
            systemPrompt: "This is a very long and detailed system prompt that should not be stored in full.",
            userPrompt: "And this is the user prompt with potentially sensitive business context.");

        await _runner.RunAsync(request);

        Assert.Single(_logsWritten);
        var log = _logsWritten[0];

        // Full prompts should never appear in persisted logs.
        Assert.DoesNotContain("This is a very long", log.PromptPreview ?? string.Empty);
        Assert.DoesNotContain("And this is the user prompt", log.OutputPreview ?? string.Empty);
    }

    [Fact]
    public async Task RunAsync_ContextWithSecretKey_IsRedactedInUserContent()
    {
        var context = new Dictionary<string, string>
        {
            ["OrganizationName"] = "Acme Corp",
            ["ApiKey"] = "sk-1234567890abcdef"
        };

        var request = CreateRequest(context: context);
        var result = await _runner.RunAsync(request);

        Assert.True(result.Succeeded);

        // The runner should not leak the raw API key into logs.
        foreach (var entry in result.Logs)
        {
            Assert.DoesNotContain("sk-1234567890abcdef", entry.Message);
        }
    }

    [Fact]
    public async Task WorkflowRunner_DelegatesToAgentRunner()
    {
        var logWriter = new InMemoryAgentRunLogWriter(_logsWritten);
        var agentRunner = (IAgentRunner)new AgentFrameworkAgentRunner(_providerFactory, logWriter);
        var workflowRunner = new AgentFrameworkWorkflowRunner(agentRunner);

        var request = CreateWorkflowRequest();
        var result = await workflowRunner.RunAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal("Test agent response content.", result.Content);
    }

    private static AgentRunRequest CreateRequest(
        Guid? providerProfileId = null,
        string systemPrompt = "You are a test agent.",
        string userPrompt = "Do something useful.",
        Dictionary<string, string>? context = null)
    {
        return new AgentRunRequest(
            ProviderProfileId: providerProfileId ?? Guid.NewGuid(),
            AgentKey: "test-agent",
            SystemPrompt: systemPrompt,
            UserPrompt: userPrompt,
            Context: context ?? new Dictionary<string, string>(),
            Options: new AgentRunOptions(
                Temperature: null,
                MaxOutputTokens: null,
                RequireStructuredOutput: false,
                OutputSchemaJson: null));
    }

    private static AgentWorkflowRunRequest CreateWorkflowRequest()
    {
        return new AgentWorkflowRunRequest(
            WorkflowKey: "test-workflow",
            ProviderProfileId: Guid.NewGuid(),
            SystemPrompt: "You are a workflow agent.",
            UserPrompt: "Execute the workflow step.",
            Context: new Dictionary<string, string>(),
            Options: new AgentRunOptions(
                Temperature: null,
                MaxOutputTokens: null,
                RequireStructuredOutput: false,
                OutputSchemaJson: null));
    }

    private sealed class InMemoryAgentRunLogWriter : IAgentRunLogWriter
    {
        private readonly List<AgentRunLog> _logs;

        public InMemoryAgentRunLogWriter(List<AgentRunLog> logs)
        {
            _logs = logs;
        }

        public Task WriteAsync(AgentRunLog log, CancellationToken cancellationToken = default)
        {
            _logs.RemoveAll(x => x.Id == log.Id);
            _logs.Add(log);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingLlmProviderFactory : ILlmProviderFactory
    {
        public Task<IChatClient> CreateChatClientAsync(
            Guid providerProfileId,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Simulated provider failure.");
        }
    }

    private sealed class UsageChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "usage response"))
            {
                Usage = new UsageDetails
                {
                    InputTokenCount = 12,
                    OutputTokenCount = 34
                }
            };

            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return AsyncEnumerable.Empty<ChatResponseUpdate>();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
