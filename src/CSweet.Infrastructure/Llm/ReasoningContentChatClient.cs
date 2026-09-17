using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace CSweet.Infrastructure.Llm;

// M.E.AI reads reasoning_content but 10.10.0 drops it when serializing assistant history.
// Preserve it at the HTTP boundary, leaving the SDK in charge of all other content and options.
internal sealed class ReasoningContentChatClient(ChatClient sdkClient) : DelegatingChatClient(sdkClient.AsIChatClient())
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var history = messages.ToList();
        using var client = CreateRequestClient(history);
        return await client.GetResponseAsync(history, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var history = messages.ToList();
        using var client = CreateRequestClient(history);
        await foreach (var update in client.GetStreamingResponseAsync(history, options, cancellationToken))
            yield return update;
    }

    private IChatClient CreateRequestClient(IReadOnlyList<ChatMessage> history)
    {
        // Each call owns its policy and history. Concurrent requests and SDK retries cannot
        // reuse another conversation's reasoning. The underlying SDK transport remains shared.
        var client = sdkClient.AsIChatClient();
        var reasoning = history.Where(message => message.Role == ChatRole.Assistant).Select(message =>
        {
            var parts = message.Contents.OfType<TextReasoningContent>().ToArray();
            return parts.Length == 0 ? null : string.Concat(parts.Select(part => part.Text));
        }).ToArray();
        if (reasoning.Any(text => text is not null))
        {
#pragma warning disable MEAI001 // OpenAI request-policy extension hook.
            client.GetRequiredService<OpenAIRequestPolicies>().AddPolicy(new ReasoningPolicy(reasoning));
#pragma warning restore MEAI001
        }
        return client;
    }

    private sealed class ReasoningPolicy(string?[] reasoning) : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            using var buffer = new MemoryStream();
            var original = message.Request.Content!;
            original.WriteTo(buffer, message.CancellationToken);
            using var content = Patch(buffer);
            message.Request.Content = content;
            try { ProcessNext(message, pipeline, currentIndex); }
            finally { message.Request.Content = original; }
        }

        public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            using var buffer = new MemoryStream();
            var original = message.Request.Content!;
            await original.WriteToAsync(buffer, message.CancellationToken);
            using var content = Patch(buffer);
            message.Request.Content = content;
            try { await ProcessNextAsync(message, pipeline, currentIndex); }
            finally { message.Request.Content = original; }
        }

        private BinaryContent Patch(MemoryStream buffer)
        {
            buffer.Position = 0;
            var body = JsonNode.Parse(buffer)!.AsObject();
            var assistants = body["messages"]!.AsArray()
                .Where(message => message?["role"]?.GetValue<string>() == "assistant").ToArray();
            if (assistants.Length != reasoning.Length)
                throw new InvalidOperationException("The provider request changed assistant history while preserving reasoning content.");
            for (var index = 0; index < assistants.Length; index++)
            {
                // Preserve a provider-native field if the SDK starts supporting it directly.
                if (reasoning[index] is { } text && assistants[index]!["reasoning_content"] is null)
                    assistants[index]!["reasoning_content"] = text;
            }
            return BinaryContent.Create(BinaryData.FromString(body.ToJsonString()));
        }
    }
}
