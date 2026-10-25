using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.Extensions.AI;

namespace CSweet.AgentHost.Broker;

internal static class ModelStreamCoalescer
{
    // Coalesce plain text or plain reasoning of the same kind. Signed/protected
    // reasoning, metadata, tool, usage and terminal updates retain their boundaries.
    // The caller persists each resulting chunk before forwarding it.
    internal static async IAsyncEnumerable<ChatResponseUpdate> ReadAsync(
        IAsyncEnumerable<ChatResponseUpdate> source, [EnumeratorCancellation] CancellationToken token)
    {
        await using var iterator = source.GetAsyncEnumerator(token);
        var text = new StringBuilder();
        ChatResponseUpdate? template = null;
        var count = 0;
        var first = true;
        var sinceFlush = Stopwatch.StartNew();
        while (true)
        {
            ChatResponseUpdate? update = null;
            Exception? failure = null;
            try { if (await iterator.MoveNextAsync()) update = iterator.Current; }
            catch (Exception error) { failure = error; }
            var ordinary = IsPlainFragment(update);
            if (text.Length > 0 && (!ordinary || update!.Role != template!.Role ||
                update.ResponseId != template.ResponseId || update.MessageId != template.MessageId ||
                update.ModelId != template.ModelId || update.AuthorName != template.AuthorName ||
                update.Contents[0].GetType() != template.Contents[0].GetType()))
            {
                yield return Chunk(template!, text.ToString());
                text.Clear(); count = 0; sinceFlush.Restart();
            }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            if (update is null) yield break;
            if (!ordinary || first) { first = false; yield return update; sinceFlush.Restart(); continue; }
            if (text.Length == 0) template = update;
            text.Append(update.Contents[0] is TextReasoningContent reasoning ? reasoning.Text : update.Text); count++;
            if (count >= 32 || text.Length >= 4096 || sinceFlush.Elapsed >= TimeSpan.FromMilliseconds(500))
            {
                yield return Chunk(template!, text.ToString());
                text.Clear(); count = 0; sinceFlush.Restart();
            }
        }
    }

    private static bool IsPlainFragment(ChatResponseUpdate? update)
    {
        if (update is null || update.Contents.Count != 1 || update.FinishReason is not null ||
            update.AdditionalProperties is { Count: > 0 }) return false;
        var content = update.Contents[0];
        if (content.Annotations is { Count: > 0 } || content.AdditionalProperties is { Count: > 0 }) return false;
        return content.GetType() == typeof(TextContent) ||
            (content.GetType() == typeof(TextReasoningContent) && ((TextReasoningContent)content).ProtectedData is null);
    }

    private static ChatResponseUpdate Chunk(ChatResponseUpdate template, string text)
    {
        var chunk = template.Clone();
        chunk.Contents = [template.Contents[0] is TextReasoningContent
            ? new TextReasoningContent(text) : new TextContent(text)];
        chunk.RawRepresentation = null;
        return chunk;
    }
}
