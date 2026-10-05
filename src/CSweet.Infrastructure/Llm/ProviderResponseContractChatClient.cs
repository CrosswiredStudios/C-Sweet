using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace CSweet.Infrastructure.Llm;

public sealed class LlmResponseContractException(string failureCode, UsageDetails? usage = null) : InvalidOperationException(
    failureCode == "llm.tool_protocol"
        ? "The model returned tool-call syntax as reasoning instead of structured tool calls after one corrective request. Check the model server's tool-call and reasoning format, then retry the retained work."
        : "The model returned no usable answer or structured tool call after one corrective request. Check the selected model's response format and output budget, then retry the retained work.")
{
    public string FailureCode { get; } = failureCode;
    public UsageDetails? Usage { get; } = usage;
}

// One provider-independent contract for all agents. Never interpret reasoning as an action,
// and never retry a response that already contains usable output or a structured tool call.
internal sealed class ProviderResponseContractChatClient(IChatClient inner, bool supportsStreaming = true)
    : DelegatingChatClient(inner)
{
    private const string Correction = "The previous request returned no usable answer or structured tool call. " +
        "Continue the supplied task using the defined structured tools when needed, or provide a final answer. " +
        "Do not put tool-call syntax only inside reasoning. Do not claim actions or validation that have not occurred.";

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var history = messages.ToList();
        UsageDetails? priorUsage = null;
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await InnerClient.GetResponseAsync(Prepare(history, attempt), options, cancellationToken);
            var state = new ResponseState(options);
            foreach (var message in response.Messages) state.Observe(message.Contents);
            var failure = state.FailureCode;
            if (priorUsage is not null)
            {
                var total = new UsageDetails();
                total.Add(priorUsage);
                if (response.Usage is not null) total.Add(response.Usage);
                response.Usage = total;
            }
            if (failure is null) return response;
            if (attempt == 1) throw new LlmResponseContractException(failure, response.Usage);
            priorUsage = response.Usage;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!supportsStreaming)
        {
            ChatResponse? response = null;
            Exception? failure = null;
            try { response = await GetResponseAsync(messages, options, cancellationToken); }
            catch (Exception error) { failure = error; }
            if (failure is not null)
            {
                if (failure is LlmResponseContractException { Usage: { } usage })
                    yield return new ChatResponseUpdate(null, [new UsageContent(usage)]);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
            foreach (var update in response!.ToChatResponseUpdates()) yield return update;
            yield break;
        }
        var history = messages.ToList();
        UsageDetails? priorUsage = null;
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = new ResponseState(options);
            UsageDetails? attemptUsage = null;
            Exception? streamError = null;
            await using var updates = InnerClient.GetStreamingResponseAsync(Prepare(history, attempt), options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool moved;
                try { moved = await updates.MoveNextAsync(); }
                catch (Exception error) { streamError = error; break; }
                if (!moved) break;
                var update = updates.Current;
                state.Observe(update.Contents);
                // Hold the last cumulative usage snapshot until the response is classified.
                // Emit one total for the logical request, avoiding double counting by consumers.
                var usages = update.Contents.OfType<UsageContent>().ToArray();
                if (usages.Length > 0)
                {
                    attemptUsage = usages[^1].Details;
                    update = update.Clone();
                    update.Contents = update.Contents.Where(x => x is not UsageContent).ToList();
                }
                yield return update;
            }
            var totalUsage = new UsageDetails();
            if (priorUsage is not null) totalUsage.Add(priorUsage);
            if (attemptUsage is not null) totalUsage.Add(attemptUsage);
            if (streamError is not null)
            {
                if (priorUsage is not null || attemptUsage is not null)
                    yield return new ChatResponseUpdate(null, [new UsageContent(totalUsage)]);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(streamError).Throw();
            }
            var failure = state.FailureCode;
            if ((failure is null || attempt == 1) && (priorUsage is not null || attemptUsage is not null))
                yield return new ChatResponseUpdate(null, [new UsageContent(totalUsage)]);
            if (failure is null) yield break;
            if (attempt == 1) throw new LlmResponseContractException(failure);
            priorUsage = attemptUsage;
        }
    }

    private static IReadOnlyList<ChatMessage> Prepare(IReadOnlyList<ChatMessage> history, int attempt) =>
        attempt == 0 ? history : [.. history, new(ChatRole.User, Correction)];

    private sealed class ResponseState(ChatOptions? options)
    {
        private bool usable, toolSyntax;
        private string reasoningTail = "";
        public string? FailureCode => usable ? null :
            toolSyntax && options?.Tools?.Count > 0 ? "llm.tool_protocol" : "llm.response_invalid";

        public void Observe(IEnumerable<AIContent> contents)
        {
            foreach (var content in contents)
            {
                if (content is TextReasoningContent reasoning)
                {
                    // Detect split markers without retaining another copy of the reasoning.
                    var text = reasoningTail + reasoning.Text;
                    toolSyntax |= text.Contains("<tool_call|>", StringComparison.Ordinal) ||
                        text.Contains("<|tool_call>", StringComparison.Ordinal) ||
                        text.Contains("<tool_call>", StringComparison.Ordinal);
                    reasoningTail = text.Length > 16 ? text[^16..] : text;
                }
                else if (content is TextContent text) usable |= !string.IsNullOrWhiteSpace(text.Text);
                else if (content is not UsageContent) usable = true;
            }
        }
    }
}
