using Microsoft.Extensions.AI;

namespace CSweet.Infrastructure.Llm;

// Some compatible-server templates require a user turn even for framework-generated
// summarization requests. Preserve every existing role, content item and tool-call pair.
internal sealed class UserQueryChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    internal static IReadOnlyList<ChatMessage> EnsureUserQuery(IEnumerable<ChatMessage> messages)
    {
        var prepared = messages.ToList();
        if (prepared.Any(message => message.Role == ChatRole.User)) return prepared;
        var index = prepared.FindIndex(message => message.Role != ChatRole.System && message.Role != new ChatRole("developer"));
        prepared.Insert(index < 0 ? prepared.Count : index,
            new ChatMessage(ChatRole.User, "Please carry out the task described in the supplied context."));
        return prepared;
    }

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(EnsureUserQuery(messages), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(EnsureUserQuery(messages), options, cancellationToken);
}
