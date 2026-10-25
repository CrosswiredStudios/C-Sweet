using CSweet.Contracts.Core;
using CSweet.Domain.Setup;

namespace CSweet.Infrastructure.Core;

public sealed partial class CurrentActivityService
{
    private static CurrentActivityGeneration? ReadGeneration(AgentRunLog? run, DateTimeOffset now)
    {
        if (run is not { Status: "Running", CompletedAt: null, GenerationStartedAt: { } started,
            GenerationObservedAt: { } observed, GenerationOutputTokens: > 0, GenerationIsEstimated: { } estimated } ||
            observed > now || now - observed >= TimeSpan.FromSeconds(15) || observed - started < TimeSpan.FromSeconds(1))
            return null;
        var rate = run.GenerationOutputTokens.Value / (observed - started).TotalSeconds;
        return double.IsFinite(rate) ? new(run.Id, rate, estimated, observed) : null;
    }
}
