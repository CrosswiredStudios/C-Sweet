using CSweet.Contracts.Core;

namespace CSweet.Contracts.Agents;

public sealed record RunInspectorNode(string Id, string Kind, string Label, DateTimeOffset OccurredAt,
    string? ParentId, Guid? EvidenceId, string? EvidenceType, string Text, bool IsStreaming = false,
    IReadOnlyList<RunInspectorSegment>? Segments = null)
{
    public static RunInspectorNode Merge(IEnumerable<RunInspectorNode> nodes)
    {
        var group = nodes.ToArray(); var last = group[^1];
        if (!last.IsStreaming) return last;
        var segments = group.SelectMany(x => x.Segments ?? []).DistinctBy(x => x.Id)
            .OrderBy(x => x.StreamSequence ?? x.Sequence).ThenBy(x => x.Sequence).TakeLast(256).ToArray();
        var text = string.Concat(segments.Select(x => x.Text));
        return last with { Segments = segments, Text = text.Length > 32000 ? "[Earlier text omitted]\n" + text[^32000..] : text };
    }
}
public sealed record RunInspectorSegment(Guid Id, long Sequence, long? StreamSequence, string Text);
public sealed record RunInspectorUsage(long InputTokens, long OutputTokens, long CachedInputTokens,
    long ReasoningTokens, int ModelCalls, int CallsWithoutReportedUsage);
public sealed record RunInspectorResponse(Guid EmployeeId, Guid? AttemptId, Guid? ModelRunId,
    Guid? WorkItemId, IReadOnlyList<RunInspectorNode> Nodes, RunInspectorUsage Usage,
    long NextSequence, bool HasMore, bool EarlierEntriesOmitted, bool EvidenceUnavailable,
    IReadOnlyList<RunInspectorModelCall> ModelCalls);
public sealed record RunInspectorModelCall(Guid Id, string? Provider, string? Model, string Status,
    DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, long DurationMs,
    int? InputTokens, int? OutputTokens, int? CachedInputTokens, string? FailureMessage,
    string? Stage = null, int RouteAttempt = 0);
