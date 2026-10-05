using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Infrastructure.Llm;
using Microsoft.Extensions.AI;

namespace CSweet.UnitTests;

public sealed class ProviderResponseContractChatClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Corrective_request_preserves_scope_and_tools_and_counts_both_generations(bool streaming)
    {
        var executed = 0;
        var options = new ChatOptions { Instructions = "Approved scope only", MaxOutputTokens = 100,
            Tools = [AIFunctionFactory.Create(() => ++executed, "inspect")] };
        ChatMessage[] history = [new(ChatRole.User, "Continue the retained task"),
            new(ChatRole.Assistant, [new FunctionCallContent("old", "inspect", new Dictionary<string, object?>())]),
            new(ChatRole.Tool, [new FunctionResultContent("old", "Prior result")])];
        var original = JsonSerializer.Serialize(history);
        var inner = new ScriptedClient((attempt, _) => attempt == 1 ? Invalid() : ValidTool());
        using var client = new ProviderResponseContractChatClient(inner);
        var response = await Run(client, history, options, streaming);
        Assert.Equal("inspect", Assert.Single(response.Messages.SelectMany(x => x.Contents).OfType<FunctionCallContent>()).Name);
        Assert.Equal(2, inner.Requests.Count);
        Assert.Equal(0, executed);
        Assert.Equal(history, inner.Requests[0]);
        Assert.Equal(history, inner.Requests[1].Take(history.Length));
        Assert.Equal(ChatRole.User, inner.Requests[1].Last().Role);
        Assert.Contains("structured tools", inner.Requests[1].Last().Text);
        Assert.Equal(original, JsonSerializer.Serialize(history));
        Assert.All(inner.Options, actual => Assert.Same(options, actual));
        Assert.Equal(12, response.Usage!.InputTokenCount);
        Assert.Equal(5, response.Usage.OutputTokenCount);
        Assert.Equal(17, response.Usage.TotalTokenCount);
    }

    [Theory]
    [InlineData(false, "<tool_call|>")]
    [InlineData(true, "<tool_call|>")]
    [InlineData(false, "<tool_call>")]
    [InlineData(true, "<tool_call>")]
    [InlineData(false, "<|tool_call>")]
    [InlineData(true, "<|tool_call>")]
    public async Task Unusable_tool_response_stops_after_one_repair_with_a_safe_shared_diagnostic(bool streaming, string marker)
    {
        var inner = new ScriptedClient((_, _) => Invalid(marker));
        using var client = new ProviderResponseContractChatClient(inner);
        var error = await Assert.ThrowsAsync<LlmResponseContractException>(() => Run(client, [new(ChatRole.User, "task")], ToolOptions(), streaming));
        Assert.Equal(2, inner.Requests.Count);
        Assert.Equal("llm.tool_protocol", error.FailureCode);
        Assert.Contains("one corrective request", error.Message);
        Assert.DoesNotContain("private-provider-output", error.Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Valid_answers_and_tool_calls_are_never_replayed_even_with_reasoning_markers(bool streaming, bool tool)
    {
        var inner = new ScriptedClient((_, _) => tool ? ValidTool() : new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new TextReasoningContent("<tool_call>"), new TextContent("Finished")])));
        using var client = new ProviderResponseContractChatClient(inner);
        await Run(client, [new(ChatRole.User, "task")], ToolOptions(), streaming);
        Assert.Single(inner.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_completion_has_the_same_bounded_failure_without_tools(bool streaming)
    {
        using var client = new ProviderResponseContractChatClient(new ScriptedClient((_, _) => new ChatResponse([])));
        var error = await Assert.ThrowsAsync<LlmResponseContractException>(() => Run(client, [], null, streaming));
        Assert.Equal("llm.response_invalid", error.FailureCode);
    }

    [Fact]
    public async Task Nonstreaming_provider_presents_the_same_agent_stream_without_requesting_sse()
    {
        var inner = new ScriptedClient((attempt, _) => attempt == 1 ? Invalid() : ValidTool());
        using var client = new ProviderResponseContractChatClient(inner, supportsStreaming: false);
        var response = await Run(client, [], ToolOptions(), true);
        Assert.Equal(2, inner.Requests.Count);
        Assert.Equal(0, inner.Streams);
        Assert.Single(response.Messages.SelectMany(x => x.Contents).OfType<FunctionCallContent>());
        Assert.Equal(12, response.Usage!.InputTokenCount);
    }

    [Fact]
    public async Task Nonstreaming_failure_still_reports_known_usage_for_both_generations()
    {
        using var client = new ProviderResponseContractChatClient(new ScriptedClient((_, _) => Invalid()), supportsStreaming: false);
        var usages = new List<UsageDetails>();
        await Assert.ThrowsAsync<LlmResponseContractException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync([], ToolOptions()))
                usages.AddRange(update.Contents.OfType<UsageContent>().Select(x => x.Details));
        });
        Assert.Equal(10, Assert.Single(usages).InputTokenCount);
        Assert.Equal(4, usages[0].OutputTokenCount);
    }

    [Fact]
    public async Task Concurrent_requests_have_independent_recovery_budgets_and_usage()
    {
        var inner = new ScriptedClient((_, history) => history.Last().Text.Contains("structured tools", StringComparison.Ordinal)
            ? ValidTool() : Invalid());
        using var client = new ProviderResponseContractChatClient(inner);
        var results = await Task.WhenAll(client.GetResponseAsync([new(ChatRole.User, "First task")], ToolOptions()),
            client.GetResponseAsync([new(ChatRole.User, "Second task")], ToolOptions()));
        Assert.Equal(4, inner.Requests.Count);
        Assert.All(results, result => Assert.Equal(12, result.Usage!.InputTokenCount));
        Assert.Equal(2, inner.Requests.Count(x => x.Count == 1));
        Assert.Equal(2, inner.Requests.Count(x => x.Count == 2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_is_preserved_and_does_not_start_a_correction(bool streaming)
    {
        using var cancellation = new CancellationTokenSource();
        var inner = new ScriptedClient((_, _) => { cancellation.Cancel(); return Invalid(); });
        using var client = new ProviderResponseContractChatClient(inner);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(client, [], ToolOptions(), streaming, cancellation.Token));
        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task Transport_failure_after_tool_output_is_not_replayed()
    {
        var inner = new ScriptedClient((_, _) => ValidTool()) { FailAfterStream = true };
        using var client = new ProviderResponseContractChatClient(inner);
        var seen = new List<FunctionCallContent>();
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync([], ToolOptions()))
                seen.AddRange(update.Contents.OfType<FunctionCallContent>());
        });
        Assert.Single(seen);
        Assert.Single(inner.Requests);
    }

    private static ChatOptions ToolOptions() => new() { Tools = [AIFunctionFactory.Create(() => "ok", "inspect")] };
    private static Task<ChatResponse> Run(IChatClient client, IEnumerable<ChatMessage> messages, ChatOptions? options,
        bool streaming, CancellationToken token = default) => streaming
        ? client.GetStreamingResponseAsync(messages, options, token).ToChatResponseAsync(token)
        : client.GetResponseAsync(messages, options, token);
    private static ChatResponse Invalid(string marker = "<tool_call|>") => new(new ChatMessage(ChatRole.Assistant,
        [new TextReasoningContent(marker[..4]), new TextReasoningContent(marker[4..] + " private-provider-output")]))
        { Usage = new() { InputTokenCount = 5, OutputTokenCount = 2, TotalTokenCount = 7 } };
    private static ChatResponse ValidTool() => new(new ChatMessage(ChatRole.Assistant,
        [new FunctionCallContent("new", "inspect", new Dictionary<string, object?>())]))
        { Usage = new() { InputTokenCount = 7, OutputTokenCount = 3, TotalTokenCount = 10 } };

    private sealed class ScriptedClient(Func<int, IReadOnlyList<ChatMessage>, ChatResponse> respond) : IChatClient
    {
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
        public List<ChatOptions?> Options { get; } = [];
        public int Streams { get; private set; }
        public bool FailAfterStream { get; init; }
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            var history = messages.ToArray();
            int attempt;
            lock (Requests) { Requests.Add(history); Options.Add(options); attempt = Requests.Count; }
            return respond(attempt, history);
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Streams++;
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates()) yield return update;
            if (FailAfterStream) throw new HttpRequestException("Interrupted after a tool call");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
