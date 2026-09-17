using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace CSweet.Infrastructure.Core;

internal sealed class UsageCapturingChatClient(IChatClient inner, Func<UsageCapturingChatClient.UsageCall, Task>? persist = null) : IChatClient
{
    public List<UsageCall> Calls { get; } = [];
    public sealed class UsageCall
    {
        public Guid Id { get; } = Guid.NewGuid();
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedAt { get; set; }
        public string Status { get; set; } = "Running";
        public UsageDetails? Usage { get; set; }
        public int MessageCharacters { get; init; }
        public int InstructionCharacters { get; init; }
        public int ToolCharacters { get; init; }
    }
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
        var call = CapturePrompt(messageList, options); Calls.Add(call);
        if (persist is not null) await persist(call);
        try
        {
            var response = await inner.GetResponseAsync(messageList, options, cancellationToken);
            call.Usage = response.Usage; call.Status = "Completed";
            if (response.Usage is not null) Usage.Add(response.Usage);
            return response;
        }
        catch (OperationCanceledException) { call.Status = "Cancelled"; throw; }
        catch { call.Status = "Failed"; throw; }
        finally { call.CompletedAt = DateTimeOffset.UtcNow; if (persist is not null) await persist(call); }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        var call = CapturePrompt(messageList, options); Calls.Add(call);
        if (persist is not null) await persist(call);
        try
        {
            await foreach (var update in inner.GetStreamingResponseAsync(messageList, options, cancellationToken))
            {
                // Streaming usage updates are cumulative for one response, not new invocations.
                foreach (var usage in update.Contents.OfType<UsageContent>()) call.Usage = usage.Details;
                yield return update;
            }
            call.Status = "Completed";
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested) call.Status = "Cancelled";
            else if (call.Status == "Running") call.Status = "Failed";
            call.CompletedAt = DateTimeOffset.UtcNow;
            if (call.Usage is not null) Usage.Add(call.Usage);
            if (persist is not null) await persist(call);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this)
            ? this
            : inner.GetService(serviceType, serviceKey);

    public void Dispose()
    {
    }

    private UsageCall CapturePrompt(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var messageCharacters = messages.Sum(message =>
            message.Text?.Length ?? message.Contents.Sum(ContentCharacters));
        var instructionCharacters = options?.Instructions?.Length ?? 0;
        var toolCharacters = options?.Tools?.OfType<AIFunctionDeclaration>().Sum(tool =>
            tool.Name.Length + tool.Description.Length + tool.JsonSchema.GetRawText().Length) ?? 0;
        MessageCharacters += messageCharacters; InstructionCharacters += instructionCharacters; ToolCharacters += toolCharacters;
        return new UsageCall { MessageCharacters = messageCharacters, InstructionCharacters = instructionCharacters, ToolCharacters = toolCharacters };
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
