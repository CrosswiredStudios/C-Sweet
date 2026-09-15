using CSweet.Infrastructure.Llm;
using Microsoft.Extensions.AI;

namespace CSweet.UnitTests;

public sealed class UserQueryChatClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_user_turn_is_added_without_rewriting_instructions_or_tool_history(bool streaming)
    {
        ChatMessage[] messages = [new(ChatRole.System, "Summarize this conversation."),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?>())]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "file contents")])];
        var options = new ChatOptions { Instructions = "Keep the summary concise." };
        var inner = new Recorder(); using var client = new UserQueryChatClient(inner);
        if (streaming) { await foreach (var _ in client.GetStreamingResponseAsync(messages, options)) { } }
        else await client.GetResponseAsync(messages, options);
        Assert.Equal(4, inner.Messages!.Count);
        Assert.Same(messages[0], inner.Messages[0]); Assert.Equal(ChatRole.User, inner.Messages[1].Role);
        Assert.Same(messages[1], inner.Messages[2]); Assert.Same(messages[2], inner.Messages[3]);
        Assert.Same(options, inner.Options); Assert.Equal(3, messages.Length);
        Assert.Equal(4, UserQueryChatClient.EnsureUserQuery(inner.Messages).Count);
    }

    [Fact]
    public void Existing_user_content_is_unchanged_and_empty_requests_gain_a_user_turn()
    {
        ChatMessage[] messages = [new(ChatRole.User, "Implement the task"), new(ChatRole.Assistant, "Working")];
        Assert.Equal(messages, UserQueryChatClient.EnsureUserQuery(messages));
        Assert.Equal(ChatRole.User, Assert.Single(UserQueryChatClient.EnsureUserQuery([])).Role);
    }

    private sealed class Recorder : IChatClient
    {
        public List<ChatMessage>? Messages;
        public ChatOptions? Options;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Messages = messages.ToList(); Options = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary")));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Messages = messages.ToList(); Options = options; await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "summary");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
