using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;

namespace CSweet.Infrastructure.Agents;

/// <summary>What decision compaction did, recorded in <c>InferenceSettingsJson.decisionCompaction</c>. Never a key.</summary>
public sealed record HarnessDecisionStats(
    [property: JsonPropertyName("engine")] string? Engine = null,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("requests")] int? Requests = null,
    [property: JsonPropertyName("kept")] int? Kept = null,
    [property: JsonPropertyName("truncated")] int? Truncated = null,
    [property: JsonPropertyName("dropped")] int? Dropped = null,
    [property: JsonPropertyName("pinned")] int? Pinned = null,
    [property: JsonPropertyName("cached")] int? Cached = null,
    [property: JsonPropertyName("stage")] int? Stage = null,
    [property: JsonPropertyName("stateTokens")] long? StateTokens = null,
    [property: JsonPropertyName("inputTokens")] long? InputTokens = null,
    [property: JsonPropertyName("outputTokens")] long? OutputTokens = null,
    [property: JsonPropertyName("ms")] long? Milliseconds = null,
    [property: JsonPropertyName("skipped")] string? Skipped = null)
{
    public static HarnessDecisionStats Skip(string reason, HarnessDecisionEngine? engine = null) =>
        new(engine?.Available == true ? engine.Host : null, engine?.Available == true ? engine.Model : null, Skipped: reason);
}

public sealed record HarnessDecisionCompaction(IReadOnlyList<HarnessContextChange> Changes, HarnessDecisionStats Stats);

/// <summary>Bounded per-process decision cache, so re-sent agent history is not re-judged on every request.</summary>
public sealed class HarnessDecisionCache : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 20_000 });
    internal bool TryGet(string key, out (double Call, double Result) value) => _cache.TryGetValue(key, out value);
    internal void Set(string key, (double Call, double Result) value) =>
        _cache.Set(key, value, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(30) });
    public void Dispose() => _cache.Dispose();
}

/// <summary>
/// Decision-model compaction, ported from nur-cli <c>typesafe::compact</c>: for each old tool call the engine is
/// asked whether the call and its verbatim result still matter. Nothing is summarized. Text, the first message and
/// the newest messages are never touched; a dropped call always takes its result with it. A probability is not
/// proof: a missing answer keeps the content, and any failure leaves the messages unchanged.
/// </summary>
public sealed class HarnessDecisionCompactor(HarnessDecisionClient client, HarnessDecisionEngine engine,
    HarnessDecisionModelOptions options, HarnessDecisionCache cache)
{
    public const string Redacted = "[redacted: this looked like a credential and was not sent]";
    public const int PreviewCharacters = 1200;
    private const string Context = "An agent transcript is being compacted to free model context. `transcript` is the whole " +
        "conversation so far, oldest first; tool outputs are replaced by a short note and long texts may be abridged. `goal` " +
        "is what the user most recently asked for. Each question asks whether one tool call, or the full output of that call, " +
        "still needs to stay in the transcript verbatim. Whatever is not kept is removed from the model's view, but the agent " +
        "can always re-run a tool or re-read a source.";

    public HarnessDecisionEngine Engine => engine;

    /// <summary>nur's reserve: 15% of the window, at least 16,384, unless that floor would take half the window.</summary>
    public static long Reserve(long window)
    {
        var proportional = (long)Math.Floor(0.15 * window);
        var reserve = Math.Max(proportional, 16384);
        return reserve >= window / 2.0 ? proportional : reserve;
    }

    public static bool IsUnderPressure(long estimatedTokens, long window) => window > 0 && estimatedTokens > window - Reserve(window);

    private sealed class Pair
    {
        public required string CallId { get; init; }
        public required string Tool { get; init; }
        public required string Arguments { get; init; }
        public required int CallMessage { get; init; }
        public int? ResultMessage { get; set; }
        public string Result { get; set; } = "";
        public bool IsError => Result.TrimStart().StartsWith("error:", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<HarnessDecisionCompaction> CompactAsync(Guid installation, List<ChatMessage> messages, CancellationToken token)
    {
        if (!engine.Available) return new([], HarnessDecisionStats.Skip(engine.Reason ?? "unavailable"));
        var started = Stopwatch.StartNew();
        var pairs = Collect(messages);
        if (pairs.Count == 0) return new([], HarnessDecisionStats.Skip("no tool calls", engine));
        bool Pinned(int message) => message == 0 || message >= messages.Count - options.PreserveRecent;
        var judged = pairs.Where(x => x.ResultMessage is { } r && !Pinned(r) && !Pinned(x.CallMessage)).ToList();
        var pinned = pairs.Count(x => x.ResultMessage is not null) - judged.Count;
        if (judged.Count == 0) return new([], HarnessDecisionStats.Skip("no eligible tool calls", engine) with { Pinned = pinned });

        var decisions = new Dictionary<Pair, (double? Call, double? Result)>();
        var uncached = new List<Pair>();
        foreach (var pair in judged)
            if (cache.TryGet(CacheKey(installation, pair), out var hit)) decisions[pair] = (hit.Call, hit.Result);
            else uncached.Add(pair);

        int? stage = null; long? stateTokens = null; var requests = 0; long input = 0, output = 0;
        if (uncached.Count > 0)
        {
            if (Fit(messages, pairs, Pinned) is not { } fitted)
                return new([], HarnessDecisionStats.Skip("state too large", engine) with { Pinned = pinned });
            stage = fitted.Stage;
            var state = new JsonObject
            {
                ["context"] = new JsonObject { ["context"] = Context, ["goal"] = Goal(messages), ["transcript"] = fitted.Transcript },
                ["calls"] = new JsonArray(uncached.Select(x => (JsonNode)new JsonObject
                {
                    ["tool"] = x.Tool, ["arguments"] = Preview(x.Arguments), ["result"] = Preview(x.Result),
                }).ToArray()),
            };
            var questions = uncached.SelectMany((x, n) => Questions(n, x.Tool)).ToArray();
            HarnessDecisionAnswers answers;
            try { answers = await client.AskAsync(engine, state, questions, token); }
            catch (HarnessDecisionException error)
            { return new([], HarnessDecisionStats.Skip(error.Message, engine) with { Pinned = pinned, Stage = stage, Milliseconds = started.ElapsedMilliseconds }); }
            requests = answers.Requests; input = answers.InputTokens; output = answers.OutputTokens; stateTokens = answers.StateTokens;
            for (var n = 0; n < uncached.Count; n++)
            {
                double? call = answers.Answers.TryGetValue($"keep_call_{n}", out var c) ? c : null;
                double? result = answers.Answers.TryGetValue($"keep_result_{n}", out var r) ? r : null;
                decisions[uncached[n]] = (call, result);
                if (call is { } cv && result is { } rv) cache.Set(CacheKey(installation, uncached[n]), (cv, rv));
            }
        }

        var changes = new List<HarnessContextChange>();
        int kept = 0, truncated = 0, dropped = 0;
        var touched = new HashSet<int>();
        foreach (var pair in judged)
        {
            var (call, result) = decisions[pair];
            var bar = options.KeepThreshold;
            if (call is null || result is null || result >= bar) { kept++; continue; }
            var resultMessage = pair.ResultMessage!.Value;
            if (call >= bar)
            {
                if (pair.Result.Length <= options.TruncateHeadChars + 120) { kept++; continue; }
                var head = pair.Result[..options.TruncateHeadChars];
                var replacement = head + $"\n[… {pair.Result.Length - head.Length} chars dropped by decision-model compaction; the call above is unchanged and the tool can be re-run]";
                if (replacement.Length >= pair.Result.Length) { kept++; continue; }
                Replace(messages[resultMessage], pair.CallId, new FunctionResultContent(pair.CallId, replacement));
                changes.Add(new(pair.CallId, pair.CallId, pair.Result.Length, replacement.Length, "decision"));
                truncated++;
            }
            else
            {
                // Drop the call and its result together, so no result is left without its call.
                Remove<FunctionCallContent>(messages[pair.CallMessage], pair.CallId);
                Remove<FunctionResultContent>(messages[resultMessage], pair.CallId);
                touched.Add(pair.CallMessage); touched.Add(resultMessage);
                changes.Add(new(pair.CallId, "", pair.Arguments.Length + pair.Result.Length, 0, "decision"));
                dropped++;
            }
        }
        foreach (var index in touched.OrderDescending())
            if (messages[index].Contents.Count == 0) messages.RemoveAt(index);
        return new(changes, new(engine.Host, engine.Model, requests, kept, truncated, dropped, pinned, judged.Count - uncached.Count,
            stage, stateTokens, input, output, started.ElapsedMilliseconds));
    }

    private static List<Pair> Collect(IReadOnlyList<ChatMessage> messages)
    {
        var pairs = new List<Pair>();
        for (var m = 0; m < messages.Count; m++)
            foreach (var content in messages[m].Contents)
                if (content is FunctionCallContent call)
                    pairs.Add(new() { CallId = call.CallId, Tool = call.Name, Arguments = JsonSerializer.Serialize(call.Arguments), CallMessage = m });
                else if (content is FunctionResultContent result &&
                         pairs.LastOrDefault(x => x.CallId == result.CallId && x.ResultMessage is null) is { } open)
                {
                    open.ResultMessage = m;
                    open.Result = ResultText(result.Result);
                }
        return pairs;
    }

    internal static string ResultText(object? value) => value switch
    {
        null => "",
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? "",
        JsonElement element => element.GetRawText(),
        _ => JsonSerializer.Serialize(value),
    };

    private static void Replace(ChatMessage message, string callId, AIContent replacement)
    {
        for (var i = 0; i < message.Contents.Count; i++)
            if (message.Contents[i] is FunctionResultContent result && result.CallId == callId) { message.Contents[i] = replacement; return; }
    }

    private static void Remove<T>(ChatMessage message, string callId) where T : AIContent
    {
        for (var i = 0; i < message.Contents.Count; i++)
        {
            var id = message.Contents[i] switch { FunctionCallContent call when call is T => call.CallId, FunctionResultContent result when result is T => result.CallId, _ => null };
            if (id == callId) { message.Contents.RemoveAt(i); return; }
        }
    }

    private static IEnumerable<HarnessDecisionQuestion> Questions(int n, string tool) =>
    [
        new($"keep_call_{n}", $"Later in this session, does knowing that tool call {n} ({tool}) was made - with these arguments - still matter?",
            "the fact of this call constrains or explains later work", "it was an intermediate step whose only value was the result"),
        new($"keep_result_{n}", $"Does the result of tool call {n} still need to be kept verbatim, because re-running the tool would not reproduce it or would be costly?",
            "its contents will be needed again, or re-running is costly/impossible", "the tool can simply be re-run, or the contents are already superseded"),
    ];

    private static string Goal(IReadOnlyList<ChatMessage> messages) => string.Join("\n\n", messages
        .Where(x => x.Role == ChatRole.User && !string.IsNullOrWhiteSpace(x.Text)).TakeLast(3)
        .Select(x => Cut(RedactInline(x.Text), 1500)));

    /// <summary>Secret-shaped content never leaves; anything else is cut to the preview budget.</summary>
    internal static string Preview(string body) =>
        CSweet.Infrastructure.Setup.AuditPayloadSanitizer.RedactText(body) != body ? Redacted : Cut(body, PreviewCharacters);

    private static string RedactInline(string text) =>
        CSweet.Infrastructure.Setup.AuditPayloadSanitizer.RedactText(text).Replace("[REDACTED]", Redacted, StringComparison.Ordinal);

    private static string Cut(string text, int max) =>
        text.Length <= max ? text : $"{text[..max]}…[truncated: {max} of {text.Length} chars shown]";

    private static string CacheKey(Guid installation, Pair pair) => $"{installation:D}:{pair.CallId}:" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pair.Tool + "\n" + pair.Arguments + "\n" + pair.Result)));

    private (string Transcript, int Stage)? Fit(IReadOnlyList<ChatMessage> messages, IReadOnlyList<Pair> pairs, Func<int, bool> pinned)
    {
        // Staged exactly as nur: args 1000 → 200 → 60, then unpinned text head+tail, then a note, then dropped.
        for (var stage = 0; stage <= 5; stage++)
        {
            var transcript = Transcript(messages, pairs, pinned, stage);
            if (HarnessDecisionTokens.Estimate(transcript) <= options.MaxStateTokens) return (transcript, stage);
        }
        return null;
    }

    private static string Transcript(IReadOnlyList<ChatMessage> messages, IReadOnlyList<Pair> pairs, Func<int, bool> pinned, int stage)
    {
        var argumentBudget = stage switch { 0 => 1000, 1 => 200, _ => 60 };
        var lines = new List<string>();
        var byCall = pairs.GroupBy(x => x.CallMessage).ToDictionary(x => x.Key, x => x.ToList());
        for (var i = 0; i < messages.Count; i++)
        {
            var text = messages[i].Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                text = RedactInline(text);
                string? body = pinned(i) ? text : stage switch
                {
                    >= 5 => null,
                    4 => $"[… {text.Length} chars omitted …]",
                    3 => HeadTail(text, 400),
                    _ => text,
                };
                if (body is not null) lines.Add($"[{i}] {messages[i].Role.Value}: {body}");
            }
            foreach (var pair in byCall.GetValueOrDefault(i) ?? [])
            {
                var args = Collapse(RedactInline(pair.Arguments), argumentBudget);
                var result = pair.ResultMessage is null ? "no result yet" : $"{(pair.IsError ? "error" : "ok")}, {pair.Result.Length} chars (omitted)";
                lines.Add($"[{i}] {pair.CallId} {pair.Tool} {args} → {result}");
            }
        }
        return string.Join("\n", lines);
    }

    private static string Collapse(string text, int budget)
    {
        var one = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return one.Length <= budget ? one : one[..budget] + "…";
    }

    private static string HeadTail(string text, int budget)
    {
        if (text.Length <= budget) return text;
        var head = budget / 2;
        return $"{text[..head]}…[{text.Length - budget} chars omitted]…{text[^(budget - head)..]}";
    }
}
