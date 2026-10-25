using CSweet.Domain.Setup;
using Microsoft.Extensions.AI;

namespace CSweet.AgentHost.Broker;

// One tracker per provider response. Counts content once, regardless of chunk boundaries,
// and never stores output text or modifies official inference accounting.
internal sealed class ModelGenerationTracker(AgentRunLog run)
{
    private long characters;
    private long reportedTokens;
    private bool stopped;

    public void Observe(ChatResponseUpdate update, DateTimeOffset now)
    {
        if (stopped) return;
        if (update.FinishReason is not null || update.Contents.Any(x => x is FunctionCallContent or FunctionResultContent))
        {
            stopped = true;
            run.GenerationObservedAt = null;
            run.GenerationOutputTokens = null;
            run.GenerationIsEstimated = null;
            return;
        }

        long added = 0;
        foreach (var content in update.Contents)
            added += content switch
            {
                TextContent text => text.Text?.Length ?? 0,
                TextReasoningContent reasoning when reasoning.ProtectedData is null => reasoning.Text?.Length ?? 0,
                _ => 0
            };
        characters += added;
        var count = update.Contents.OfType<UsageContent>().LastOrDefault()?.Details.OutputTokenCount;
        var validCount = count is >= 0 && count >= reportedTokens;
        var advanced = validCount && count > reportedTokens;
        if (validCount) reportedTokens = count!.Value;
        // Empty/metadata-only chunks and repeated cumulative usage do not refresh output freshness.
        if (added == 0 && !advanced) return;

        run.GenerationStartedAt ??= now;
        run.GenerationObservedAt = now;
        run.GenerationOutputTokens = validCount ? count!.Value : characters / 4d;
        run.GenerationIsEstimated = !validCount;
    }
}
