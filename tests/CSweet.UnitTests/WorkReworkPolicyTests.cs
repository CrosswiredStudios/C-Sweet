using System.Text.Json;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.WorkManagement;
using Shared = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class WorkReworkPolicyTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static JsonElement Review(int candidate, int findings, bool first = false, bool second = false) =>
        JsonSerializer.SerializeToElement(new { sha256 = candidate.ToString("x64"),
            criteria = new[] { new { criterion = "A", satisfied = first }, new { criterion = "B", satisfied = second } },
            findings = Enumerable.Range(0, findings).Select(x => "Finding " + x).ToArray() }, Json);

    internal static WorkStageExecution Add(WorkItemExecution item, JsonElement output)
    {
        var stage = new WorkStageExecution { Id = Guid.NewGuid(), StageKey = "quality", Traversal = item.Stages.Count,
            ItemExecution = item, ItemExecutionId = item.Id, Status = WorkStageExecutionStatus.Completed };
        var attempt = new WorkExecutionAttempt { Id = Guid.NewGuid(), StageExecutionId = stage.Id, Attempt = 1,
            Status = WorkExecutionAttemptStatus.Completed };
        attempt.ResultJson = JsonSerializer.Serialize(new Shared.WorkExecutionOutcomeV1(stage.Id, attempt.Id,
            Shared.WorkExecutionDispositions.Completed, "changes_requested", "Review", output, [], []), Json);
        stage.Attempts.Add(attempt); item.Stages.Add(stage);
        item.Traversal = stage.Traversal; item.CurrentStageKey = stage.StageKey;
        return stage;
    }

    private static WorkReworkPolicy.Decision Evaluate(WorkStageExecution stage, JsonElement output) =>
        WorkReworkPolicy.Evaluate(stage, new() { FromStageKey = stage.StageKey, ToStageKey = "specialist-execution",
            OutcomeCode = "changes_requested", MaximumTraversals = 3 }, "changes_requested", output);

    [Fact]
    public void ImprovingReviewsContinuePastAnyLifetimeCycleCount()
    {
        var item = new WorkItemExecution();
        for (var cycle = 0; cycle < 12; cycle++)
        {
            var output = Review(cycle + 1, 20 - cycle);
            var decision = Evaluate(Add(item, output), output);
            Assert.True(decision.Allowed); Assert.Equal(0, decision.StagnantReviews);
        }
    }

    [Fact]
    public void CriteriaProgressResetsStagnationEvenWhenNewFindingsAreDiscovered()
    {
        var item = new WorkItemExecution();
        Add(item, Review(1, 2)); Add(item, Review(2, 2)); Add(item, Review(3, 2));
        var output = Review(4, 6, first: true);
        var decision = Evaluate(Add(item, output), output);
        Assert.True(decision.Allowed); Assert.Equal(0, decision.StagnantReviews);
    }

    [Fact]
    public void ThreeStagnantCyclesEscalateEvenWithNewCandidateIds()
    {
        var item = new WorkItemExecution();
        for (var cycle = 0; cycle < 4; cycle++)
        {
            var output = Review(cycle + 1, 3);
            var decision = Evaluate(Add(item, output), output);
            Assert.Equal(cycle < 3, decision.Allowed);
            Assert.Equal(cycle, decision.StagnantReviews);
        }
    }

    [Fact]
    public void RegressionsAndOscillationCannotResetTheBestReview()
    {
        var item = new WorkItemExecution();
        Add(item, Review(1, 4, first: true));
        Add(item, Review(2, 1)); // Fewer findings but lost a satisfied criterion.
        Add(item, Review(3, 4, first: true)); // Back where we started, not progress.
        var output = Review(4, 1);
        Assert.False(Evaluate(Add(item, output), output).Allowed);
    }

    [Fact]
    public void ReReviewingIdenticalContentCannotClaimProgress()
    {
        var item = new WorkItemExecution();
        Add(item, Review(1, 5)); Add(item, Review(1, 4)); Add(item, Review(1, 3));
        var output = Review(1, 2, first: true);
        Assert.False(Evaluate(Add(item, output), output).Allowed);
    }

    [Fact]
    public void ChangedCriteriaAndMissingEvidenceDoNotCountAsProgress()
    {
        var item = new WorkItemExecution();
        Add(item, Review(1, 5));
        var omitted = JsonSerializer.SerializeToElement(new { sha256 = new string('a',64), criteria = Array.Empty<object>(), findings = Array.Empty<string>() });
        Add(item, omitted); Add(item, omitted);
        Assert.False(Evaluate(Add(item, omitted), omitted).Allowed);
    }

    [Fact]
    public void AttemptRetriesAndUnorderedHistoryDoNotSpendAdditionalCycles()
    {
        var item = new WorkItemExecution();
        var first = Add(item, Review(1, 6));
        first.Attempts = [new() { Attempt = 0, Status = WorkExecutionAttemptStatus.Failed }, .. first.Attempts];
        Add(item, Review(2, 5)); Add(item, Review(3, 5));
        var output = Review(4, 4); var current = Add(item, output);
        item.Stages = item.Stages.Reverse().ToList();
        Assert.True(Evaluate(current, output).Allowed);
    }

    [Fact]
    public void NonReviewCyclesRetainTheirExplicitBound()
    {
        var item = new WorkItemExecution { Traversal = 3 };
        var stage = new WorkStageExecution { StageKey = "custom", ItemExecution = item };
        Assert.False(Evaluate(stage, Review(1, 1)).Allowed);
    }

    [Theory]
    [InlineData("quality", "candidateDigest")]
    [InlineData("technical-review", "sourceCommitSha")]
    public void CodeReviewsUseExactCandidateEvidence(string key, string digestProperty)
    {
        var item = new WorkItemExecution();
        for (var cycle = 0; cycle < 6; cycle++)
        {
            var value = new Dictionary<string, object> { [digestProperty] = (cycle + 1).ToString("x40"),
                ["findings"] = Enumerable.Range(0, 10 - cycle).Select(x => "Finding " + x).ToArray() };
            if (key == "quality") value["criteria"] = new[] { new { criterion = "A", satisfied = false } };
            var output = JsonSerializer.SerializeToElement(value);
            var stage = Add(item, output); stage.StageKey = key;
            Assert.True(Evaluate(stage, output).Allowed);
        }
    }

    [Fact]
    public void AcceptedReviewRecoveryRejectsMismatchedOrUnfinishedLatestAttempts()
    {
        var item = new WorkItemExecution(); var stage = Add(item, Review(1, 3));
        stage.Attempts.Add(new() { Id = Guid.NewGuid(), Attempt = 2, Status = WorkExecutionAttemptStatus.Running });
        Assert.Null(WorkReworkPolicy.CompletedOutcome(stage));
        var latest = stage.Attempts.Single(x => x.Attempt == 2);
        latest.Status = WorkExecutionAttemptStatus.Completed;
        latest.ResultJson = stage.Attempts.Single(x => x.Attempt == 1).ResultJson;
        Assert.Null(WorkReworkPolicy.CompletedOutcome(stage));
    }
}
