using CSweet.Domain.Setup;
using CSweet.Contracts.Analytics;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Analytics;

public static class InferenceMeasurements
{
    // Legacy rows remain visible, but are explicitly labelled as incomplete historical coverage.
    public static IQueryable<AgentRunLog> ProviderCalls(this IQueryable<AgentRunLog> query) =>
        query.Where(x => x.InvocationKind != "llm-queue" && x.MeasurementKind != "Queue" &&
            (x.MeasurementKind == "Legacy" || x.ProviderStartedAt != null));

    public static long Input(AgentRunLog x) => x.ReportedInputTokens ?? x.TokenInputCount ?? 0;
    public static long Output(AgentRunLog x) => x.ReportedOutputTokens ?? x.TokenOutputCount ?? 0;
    public static bool HasInput(AgentRunLog x) => x.ReportedInputTokens.HasValue || x.TokenInputCount.HasValue;
    public static bool HasOutput(AgentRunLog x) => x.ReportedOutputTokens.HasValue || x.TokenOutputCount.HasValue;

    public static async Task<EfficiencyUsage> SumAsync(this IQueryable<AgentRunLog> source, CancellationToken ct)
    {
        var totals = await source.GroupBy(x => 1).Select(g => new EfficiencyUsage(
            g.LongCount(), g.Sum(x => x.ReportedInputTokens ?? x.TokenInputCount ?? 0),
            g.Sum(x => x.ReportedOutputTokens ?? x.TokenOutputCount ?? 0),
            g.Sum(x => (long)(x.TokenCachedInputCount ?? 0)), g.Sum(x => (long)(x.TokenReasoningCount ?? 0)),
            g.LongCount(x => x.ReportedInputTokens != null || x.TokenInputCount != null),
            g.LongCount(x => x.ReportedOutputTokens != null || x.TokenOutputCount != null),
            g.LongCount(x => x.Status == "Failed"), g.LongCount(x => x.Status == "Cancelled"),
            g.LongCount(x => x.MeasurementKind == "Legacy"), g.Sum(x => x.DurationMs))
        {
            FullyReportedCalls = g.LongCount(x => (x.ReportedInputTokens != null || x.TokenInputCount != null) &&
                (x.ReportedOutputTokens != null || x.TokenOutputCount != null))
        }).FirstOrDefaultAsync(ct);
        return totals ?? new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }
}
