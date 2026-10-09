using CSweet.Memory;

namespace CSweet.Memory.Evaluate;

public sealed record RetrievalGateFailure(string Split, string Gate, string Detail);

// Candidate thresholds established before observing v3. These are engineering proposals,
// not user-approved production SLOs or evidence of generated-answer quality.
public static class EvaluationGates
{
    public const string Version = "retrieval-candidate-gates-v1";
    public const double MinimumRecall = .90;
    public const double MaximumIrrelevantRate = .15;
    public const double MinimumAbstention = 1;
    public const int MaximumRenderedCharacters = 2048;

    public static RetrievalGateFailure[] Check(IReadOnlyList<EvaluationScenario> scenarios,
        IReadOnlyList<EvaluationSample> samples, int repetitions)
    {
        if (repetitions < 1) throw new ArgumentOutOfRangeException(nameof(repetitions));
        var failures = new List<RetrievalGateFailure>();
        var rows = samples.Where(x => x.Mode == "current" && x.Phase == "warm").ToArray();
        foreach (var split in new[] { "calibration", "evaluation" })
        {
            var expected = scenarios.Where(x => x.Split == split).ToArray();
            var measured = rows.Where(x => x.Split == split).ToArray();
            if (expected.Length == 0 || !expected.Any(x => x.RequiredIds.Length > 0) || !expected.Any(x => x.RequiredIds.Length == 0))
                failures.Add(new(split, "coverage", "Positive and abstention scenarios are required."));
            var valid = expected.Length > 0 && measured.Length == expected.Length * repetitions &&
                expected.All(q => measured.Where(x => x.Scenario == q.Id).Select(x => x.Repetition).Order()
                    .SequenceEqual(Enumerable.Range(0, repetitions))) &&
                measured.All(x => expected.Any(q => q.Id == x.Scenario && x.Required == q.RequiredIds.Length));
            if (!valid) failures.Add(new(split, "measurements", "Missing, duplicated or unexpected scenario measurements."));
            if (measured.Length == 0) continue;
            var summary = EvaluationMetrics.Summarize(split, "current", measured);
            if (summary.RecallAtK < MinimumRecall)
                failures.Add(new(split, "recall", $"{summary.RecallAtK:F4} < {MinimumRecall:F2}"));
            if (summary.IrrelevantRate > MaximumIrrelevantRate)
                failures.Add(new(split, "relevance", $"{summary.IrrelevantRate:F4} > {MaximumIrrelevantRate:F2}"));
            if (summary.CorrectAbstentionRate is null || summary.CorrectAbstentionRate < MinimumAbstention)
                failures.Add(new(split, "abstention", "Every unsupported question must abstain."));
            if (measured.Any(x => x.RenderedCharacters > MaximumRenderedCharacters))
                failures.Add(new(split, "rendered-size", "Rendered context exceeds the evaluation allowance."));
        }
        // Hard safety/instrumentation gates cover cold and warm current paths, not only the aggregate.
        foreach (var row in samples.Where(x => x.Mode == "current"))
        {
            var q = scenarios.SingleOrDefault(x => x.Id == row.Scenario);
            if (q is null || q.Split != row.Split) { failures.Add(new(row.Split, "measurements", "Unknown scenario.")); continue; }
            var ids = row.ReturnedIds.Select(x => Guid.TryParse(x, out var id) ? id : Guid.Empty).ToArray();
            if (ids.Any(x => x == Guid.Empty) || ids.Distinct().Count() != ids.Length || ids.Length != row.Returned ||
                row.Required != q.RequiredIds.Length || row.RetrievedRequired != ids.Intersect(q.RequiredIds).Count() ||
                row.Irrelevant != ids.Except(q.RelevantIds ?? q.RequiredIds).Count() ||
                row.CorrectAbstention != (q.RequiredIds.Length == 0 && ids.Length == 0) ||
                row.Forbidden < 0 || row.RawForbidden < row.Forbidden || row.DatabaseCommands < 0 ||
                row.RenderedCharacters < 0 || row.EstimatedTokens < 0 || !double.IsFinite(row.Milliseconds) || row.Milliseconds < 0)
                failures.Add(new(row.Split, "measurements", row.Scenario + ": inconsistent measurements."));
            // Store candidates may carry restricted sensitivity for the production policy
            // to withhold. Unauthorized release is measured after that policy and packing.
            if (row.Forbidden > 0 || ids.Intersect(q.ForbiddenIds).Any())
                failures.Add(new(row.Split, "forbidden", row.Scenario + ": forbidden evidence returned."));
            if (MemoryLexicalQuery.Parse(q.Query).Terms.Count > 0 && row.DatabaseCommands == 0)
                failures.Add(new(row.Split, "instrumentation", row.Scenario + ": no database commands observed."));
        }
        return failures.Distinct().ToArray();
    }
}
