namespace CSweet.Memory.Evaluate;

public sealed record EvaluationSample(string Scenario, string Split, string Mode, int Required, int RetrievedRequired,
    int Returned, int Irrelevant, int Forbidden, bool CorrectAbstention, double Milliseconds, long DatabaseCommands,
    int RenderedCharacters, int EstimatedTokens, string[] Channels, string[] ReturnedIds);

public sealed record EvaluationSummary(string Split, string Mode, int Samples, double RecallAtK,
    double IrrelevantRate, int ForbiddenResults, int AbstentionSamples, double? CorrectAbstentionRate,
    double P50Milliseconds, double P95Milliseconds, double MeanDatabaseCommands, long MaximumDatabaseCommands,
    double MeanRenderedCharacters, int MaximumRenderedCharacters, int MaximumEstimatedTokens);

public static class EvaluationMetrics
{
    public static double Percentile(IEnumerable<double> values, double percentile)
    {
        if (percentile is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(percentile));
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) throw new ArgumentException("At least one measurement is required.", nameof(values));
        return sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    public static EvaluationSummary Summarize(string split, string mode, IReadOnlyList<EvaluationSample> samples)
    {
        var rows = samples.Where(x => x.Split == split && x.Mode == mode).ToArray();
        if (rows.Length == 0) throw new ArgumentException("No matching measurements.", nameof(samples));
        var required = rows.Sum(x => x.Required);
        var returned = rows.Sum(x => x.Returned);
        var abstentions = rows.Where(x => x.Required == 0).ToArray();
        return new(split, mode, rows.Length, required == 0 ? 1 : (double)rows.Sum(x => x.RetrievedRequired) / required,
            returned == 0 ? 0 : (double)rows.Sum(x => x.Irrelevant) / returned, rows.Sum(x => x.Forbidden),
            abstentions.Length, abstentions.Length == 0 ? null : (double)abstentions.Count(x => x.CorrectAbstention) / abstentions.Length,
            Percentile(rows.Select(x => x.Milliseconds), .5), Percentile(rows.Select(x => x.Milliseconds), .95),
            rows.Average(x => x.DatabaseCommands), rows.Max(x => x.DatabaseCommands),
            rows.Average(x => x.RenderedCharacters), rows.Max(x => x.RenderedCharacters), rows.Max(x => x.EstimatedTokens));
    }
}
