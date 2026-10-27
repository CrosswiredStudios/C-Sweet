using CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

internal static class WorkExecutionResumeHistory
{
    internal static IReadOnlyList<WorkExecutionOutcomeV1> Compact(IEnumerable<WorkExecutionOutcomeV1> orderedOutcomes)
    {
        var history = orderedOutcomes.ToArray();
        var latest = history.GroupBy(x => x.StageExecutionId).ToDictionary(x => x.Key, x => x.Last());
        // Compute callbacks are replaceable checkpoints, not independent review findings.
        // Keep their latest binding only while it is current. All attempts remain in the
        // audit store, and substantive outcomes remain available for rework and recovery.
        return history.Where(x => x.OutcomeCode != "awaiting-compute" ||
            ReferenceEquals(x, latest[x.StageExecutionId])).ToArray();
    }
}
