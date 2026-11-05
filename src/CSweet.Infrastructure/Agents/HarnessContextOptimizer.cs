using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace CSweet.Infrastructure.Agents;

public sealed record HarnessContextChange(string CallId, string RetainedCallId, int OriginalCharacters, int RemainingCharacters);

/// <summary>Request-local, deterministic compaction. No result or permission is reused across requests.</summary>
public static class HarnessContextOptimizer
{
    public const string ContextToolName = "read_historical_context";

    public static IReadOnlyList<HarnessContextChange> Spill(IReadOnlyList<ChatMessage> messages, Guid runId)
    {
        var changes = new List<HarnessContextChange>();
        for (var m = 0; m < messages.Count - 2; m++)
            for (var i = 0; i < messages[m].Contents.Count; i++)
            {
                if (messages[m].Contents[i] is not FunctionResultContent result) continue;
                var text = JsonSerializer.Serialize(result.Result);
                if (text.Length <= 16000) continue;
                var preview = CSweet.Infrastructure.Setup.AuditPayloadSanitizer.RedactText(text[..2000]);
                // Name the model-visible tool; the model never sees capability identifiers.
                var replacement = $"{preview}\n[Historical tool result shortened from {text.Length} characters. Retrieve the rest with the {ContextToolName} tool: runId={runId:D}, callId={result.CallId}, offset=0, limit up to 16000 per call; continue while hasMore is true. The snapshot does not establish current source state; read mutable sources again before acting.]";
                messages[m].Contents[i] = new FunctionResultContent(result.CallId, replacement);
                changes.Add(new(result.CallId, $"{runId:D}:{result.CallId}", text.Length, replacement.Length));
            }
        return changes;
    }

    public static IReadOnlyList<HarnessContextChange> Compact(IReadOnlyList<ChatMessage> messages)
    {
        var calls = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var call in messages.SelectMany(x => x.Contents).OfType<FunctionCallContent>())
            calls[call.CallId] = JsonSerializer.Serialize(new { call.Name, call.Arguments });
        var latest = new Dictionary<string, string>(StringComparer.Ordinal);
        var changes = new List<HarnessContextChange>();
        // Preserve recent results and every call/result envelope. Only exact duplicate read representations shrink.
        for (var messageIndex = messages.Count - 1; messageIndex >= 0; messageIndex--)
        {
            var message = messages[messageIndex];
            for (var i = message.Contents.Count - 1; i >= 0; i--)
            {
                if (message.Contents[i] is not FunctionResultContent result || !calls.TryGetValue(result.CallId, out var call)) continue;
                var text = JsonSerializer.Serialize(result.Result);
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(call + "\n" + text)));
                if (messageIndex < messages.Count - 2 && text.Length > 2048 && latest.TryGetValue(key, out var retained))
                {
                    var shortened = $"[Duplicate result compacted. The identical result for the same function and arguments is retained in call {retained}. This is a historical snapshot, not proof the source is still current. Read again before relying on mutable state.]";
                    message.Contents[i] = new FunctionResultContent(result.CallId, shortened);
                    changes.Add(new(result.CallId, retained, text.Length, shortened.Length));
                }
                else latest[key] = result.CallId;
            }
        }
        return changes;
    }
}
