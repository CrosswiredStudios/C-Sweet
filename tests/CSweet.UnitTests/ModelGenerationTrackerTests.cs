using CSweet.AgentHost.Broker;
using CSweet.Domain.Setup;
using Microsoft.Extensions.AI;

namespace CSweet.UnitTests;

public sealed class ModelGenerationTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private static ChatResponseUpdate Text(string text) => new(ChatRole.Assistant, [new TextContent(text)]);
    private static ChatResponseUpdate Usage(long count) => new(ChatRole.Assistant,
        [new UsageContent(new UsageDetails { OutputTokenCount = count })]);

    [Fact]
    public void EstimationCountsTextAndReadableReasoningOnceWithoutChangingAccounting()
    {
        var run = new AgentRunLog { StartedAt = Start.AddMinutes(-5), ReportedOutputTokens = 99 };
        var tracker = new ModelGenerationTracker(run);
        tracker.Observe(Text("abcd"), Start);
        tracker.Observe(new(ChatRole.Assistant, [new TextReasoningContent("efgh")]), Start.AddSeconds(2));
        Assert.Equal(Start, run.GenerationStartedAt);
        Assert.Equal(Start.AddSeconds(2), run.GenerationObservedAt);
        Assert.Equal(2d, run.GenerationOutputTokens);
        Assert.True(run.GenerationIsEstimated);
        Assert.Equal(99, run.ReportedOutputTokens);
        Assert.Null(run.TokenOutputCount);
    }

    [Fact]
    public void ProtectedReasoningMetadataAndRepeatedUsageDoNotInventOutputOrFreshness()
    {
        var run = new AgentRunLog(); var tracker = new ModelGenerationTracker(run);
        tracker.Observe(new(ChatRole.Assistant, [new TextReasoningContent("private") { ProtectedData = "encrypted" }]), Start);
        tracker.Observe(new(ChatRole.Assistant, []), Start.AddSeconds(1));
        Assert.Null(run.GenerationStartedAt);
        tracker.Observe(Usage(20), Start.AddSeconds(2));
        tracker.Observe(Usage(20), Start.AddSeconds(3));
        tracker.Observe(Usage(-1), Start.AddSeconds(4));
        tracker.Observe(Usage(10), Start.AddSeconds(5));
        Assert.Equal(Start.AddSeconds(2), run.GenerationObservedAt);
        Assert.Equal(20d, run.GenerationOutputTokens);
        Assert.False(run.GenerationIsEstimated);
    }

    [Fact]
    public void ProviderCountsAreCumulativeAndOnlyApplyToTheCurrentObservation()
    {
        var run = new AgentRunLog(); var tracker = new ModelGenerationTracker(run);
        tracker.Observe(Text("abcdefgh"), Start);
        tracker.Observe(Usage(100), Start.AddSeconds(2));
        tracker.Observe(Usage(150), Start.AddSeconds(3));
        Assert.Equal(150d, run.GenerationOutputTokens); // Not 100 + 150.
        Assert.False(run.GenerationIsEstimated);
        tracker.Observe(Text("ijkl"), Start.AddSeconds(4));
        Assert.Equal(3d, run.GenerationOutputTokens);
        Assert.True(run.GenerationIsEstimated);
        tracker.Observe(Usage(200), Start.AddSeconds(5));
        Assert.Equal(200d, run.GenerationOutputTokens);
        Assert.False(run.GenerationIsEstimated);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("result")]
    [InlineData("finish")]
    public void ToolHandoffAndFinishSuppressTheBadgeIncludingLaterUsage(string kind)
    {
        var run = new AgentRunLog(); var tracker = new ModelGenerationTracker(run);
        tracker.Observe(Text("abcdefgh"), Start);
        var stop = kind switch
        {
            "request" => new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call", "tool")]),
            "result" => new ChatResponseUpdate(ChatRole.Tool, [new FunctionResultContent("call", "tool output")]),
            _ => new ChatResponseUpdate(ChatRole.Assistant, []) { FinishReason = ChatFinishReason.Stop }
        };
        tracker.Observe(stop, Start.AddSeconds(2));
        tracker.Observe(Usage(500), Start.AddSeconds(3));
        tracker.Observe(Text("more"), Start.AddSeconds(4));
        Assert.Null(run.GenerationObservedAt);
        Assert.Null(run.GenerationOutputTokens);
        Assert.Null(run.GenerationIsEstimated);
    }

    [Fact]
    public void SeparateResponsesHaveIndependentStartsAndCounters()
    {
        var first = new AgentRunLog(); var second = new AgentRunLog();
        var a = new ModelGenerationTracker(first); var b = new ModelGenerationTracker(second);
        a.Observe(Usage(1000), Start);
        b.Observe(Text("abcd"), Start.AddSeconds(20));
        a.Observe(Usage(2000), Start.AddSeconds(21));
        Assert.Equal(Start.AddSeconds(20), second.GenerationStartedAt);
        Assert.Equal(1d, second.GenerationOutputTokens);
        Assert.Equal(2000d, first.GenerationOutputTokens);
    }
}
