using System.Runtime.CompilerServices;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed partial class PlatformLlmCapabilityHandlerTests
{
    [Theory]
    [InlineData("length", false, false, "llm.output_incomplete")]
    [InlineData("length", true, false, "llm.output_incomplete")]
    [InlineData("stop", false, false, "llm.empty_response")]
    [InlineData("stop", true, false, null)]
    [InlineData("tool_calls", false, true, null)]
    public async Task Incomplete_generation_preserves_usage_and_never_claims_completion(
        string finish, bool text, bool tool, string? failureCode)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var provider = await AddProviderAsync(db);
        using var client = new FinishingChatClient(finish, text, tool);
        var handler = new PlatformLlmCapabilityHandler(db, new StreamingProviderFactory(client),
            new AgentEmployeeIdentityResolver(db), new AgentInstallationConfigurationService(db, new TestAuditEventWriter()),
            [], new TestMediaAssetService(), NullLogger<PlatformLlmCapabilityHandler>.Instance);
        var results = await ReadAsync(db, handler, provider);
        var log = await db.AgentRunLogs.SingleAsync();
        Assert.Equal(16384, log.TokenOutputCount);
        if (failureCode is null)
        {
            Assert.All(results, x => Assert.True(x.Succeeded, x.Error));
            Assert.Equal("Completed", log.Status);
        }
        else
        {
            var failure = Assert.Single(results, x => !x.Succeeded);
            Assert.Equal(failureCode, failure.FailureCode);
            Assert.False(failure.Retryable); // Repeating an exhausted generation is not progress.
            Assert.Equal("Failed", log.Status);
            Assert.Contains("incomplete", log.FailureMessage);
        }
    }

    [Fact]
    public async Task Model_context_is_not_reduced_by_a_default_512_message_limit()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var provider = await AddProviderAsync(db);
        var client = new StreamingChatClient();
        var handler = new PlatformLlmCapabilityHandler(db, new StreamingProviderFactory(client),
            new AgentEmployeeIdentityResolver(db), new AgentInstallationConfigurationService(db, new TestAuditEventWriter()),
            [], new TestMediaAssetService(), NullLogger<PlatformLlmCapabilityHandler>.Instance);
        var results = await ReadAsync(db, handler, provider, messageCount: 600);
        Assert.All(results, x => Assert.True(x.Succeeded, x.Error));
        Assert.Equal(600, client.ReceivedMessages.Count);
    }

    private sealed class FinishingChatClient(string finish, bool text, bool tool) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new(ChatRole.Assistant, [new TextReasoningContent("Thinking without a final answer.")]);
            if (text) yield return new(ChatRole.Assistant, [new TextContent("An answer")]);
            if (tool) yield return new(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?>())]);
            yield return new(ChatRole.Assistant,
                [new UsageContent(new UsageDetails { InputTokenCount = 7777, OutputTokenCount = 16384 })])
                { FinishReason = new ChatFinishReason(finish) };
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
