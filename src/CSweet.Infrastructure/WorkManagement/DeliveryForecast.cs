using CSweet.Contracts.WorkManagement;
using CSweet.Domain.WorkManagement;

namespace CSweet.Infrastructure.WorkManagement;

public static class DeliveryForecast
{
    public static WorkSprintForecastResponse WithCompletionRange(WorkSprintForecastResponse forecast,
        IReadOnlyList<WorkSprintSnapshot> history, int unestimatedItems, DateTimeOffset now)
    {
        // Actual wall-clock time includes queueing, review and waits. Planned dates are never samples.
        var rates = history.Where(x => x.CompletedPoints > 0 && x.StartedAt.HasValue &&
                x.StartedAt.Value < x.CompletedAt && x.CompletedAt <= now)
            .Select(x => (x.CompletedAt - x.StartedAt!.Value).TotalSeconds / (double)x.CompletedPoints)
            .Where(x => double.IsFinite(x) && x > 0).ToArray();
        var result = forecast with { GeneratedAt = now, HistoricalSampleCount = rates.Length,
            UnestimatedItemCount = unestimatedItems };
        if (forecast.BlockingItemCount > 0)
            return result with { Basis = "Remaining work has unresolved blockers or approvals; completion timing is unknown until those dependencies resolve." };
        if (unestimatedItems > 0)
            return result with { Basis = "Remaining work includes unestimated items; completion timing is unknown." };
        if (forecast.RemainingPoints == 0)
            return result with { EarliestCompletionAt = now, LatestCompletionAt = now,
                Basis = "No estimated work remains. Acceptance and release gates still apply." };
        if (rates.Length < 2)
            return result with { Basis = "A completion range needs at least two completed increments with delivered points and actual elapsed time." };
        var fastest = rates.Min() * (double)forecast.RemainingPoints;
        var slowest = rates.Max() * (double)forecast.RemainingPoints;
        if (!double.IsFinite(slowest) || slowest > (DateTimeOffset.MaxValue - now).TotalSeconds)
            return result with { Basis = "The observed delivery rate cannot support a representable completion range." };
        return result with { EarliestCompletionAt = now.AddSeconds(fastest), LatestCompletionAt = now.AddSeconds(slowest),
            Basis = "Observed fastest-to-slowest elapsed time per delivered point on this board; assumes comparable work and capacity. This is a forecast, not a commitment or statistical confidence interval." };
    }
}
