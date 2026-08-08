using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace CSweet.Infrastructure.Core;

internal sealed class UsageCapturingChatClient(IChatClient inner) : IChatClient
{
    public UsageDetails Usage { get; } = new();
    public int MessageCharacters { get; private set; }
    public int InstructionCharacters { get; private set; }
    public int ToolCharacters { get; private set; }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        CapturePrompt(messageList, options);
        var response = await inner.GetResponseAsync(messageList, options, cancellationToken);
        if (response.Usage is not null) Usage.Add(response.Usage);
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        CapturePrompt(messageList, options);
        await foreach (var update in inner.GetStreamingResponseAsync(messageList, options, cancellationToken))
        {
            foreach (var usage in update.Contents.OfType<UsageContent>()) Usage.Add(usage.Details);
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this)
            ? this
            : inner.GetService(serviceType, serviceKey);

    public void Dispose()
    {
    }

    private void CapturePrompt(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        MessageCharacters += messages.Sum(message =>
            message.Text?.Length ?? message.Contents.Sum(ContentCharacters));
        InstructionCharacters += options?.Instructions?.Length ?? 0;
        ToolCharacters += options?.Tools?.OfType<AIFunctionDeclaration>().Sum(tool =>
            tool.Name.Length + tool.Description.Length + tool.JsonSchema.GetRawText().Length) ?? 0;
    }

    private static int ContentCharacters(AIContent content) => content switch
    {
        TextContent text => text.Text.Length,
        FunctionCallContent call => call.CallId.Length + call.Name.Length +
            (call.Arguments?.Sum(item => item.Key.Length + (item.Value?.ToString()?.Length ?? 0)) ?? 0),
        FunctionResultContent result => result.CallId.Length + (result.Result?.ToString()?.Length ?? 0),
        _ => 0
    };
}
