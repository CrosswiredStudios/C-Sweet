using System.Text.Json;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class WorkExecutionResumeHistoryTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ThousandComputeCallbacksKeepCurrentBindingAndSubstantiveHistoryWithinInboxLimit()
    {
        var item = new WorkItemExecution { WorkItem = new(), CurrentStageKey = "quality" };
        var author = new WorkStageExecution { Id = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow.AddHours(-1) };
        var qa = new WorkStageExecution { Id = Guid.NewGuid(), ItemExecution = item, StageKey = "quality", CreatedAt = DateTimeOffset.UtcNow };
        item.Stages = [qa, author];
        Add(author, 1, "completed", new { commit = new string('a', 40) });
        Add(qa, 1, "blocked", new { qaProcessingError = "Retained transport failure" });
        for (var i = 2; i <= 1001; i++)
            Add(qa, i, "awaiting-compute", new { environmentId = Guid.NewGuid(), operationIds = new[] { Guid.NewGuid() },
                sourceCommitSha = new string('b', 40), buildSha256 = new string('c', 64), retryAt = DateTimeOffset.UtcNow.AddMinutes(5) });
        qa.Attempts = qa.Attempts.Reverse().ToList();

        var outcomes = WorkOrchestrator.AssignmentOutcomes(qa);
        Assert.Equal(3, outcomes.Count);
        Assert.Equal("completed", outcomes[0].OutcomeCode);
        Assert.Equal("blocked", outcomes[1].OutcomeCode);
        Assert.Equal(qa.Attempts.Single(x => x.Attempt == 1001).Id, outcomes[^1].AttemptId);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(outcomes, Json).Length < 4096);
        Assert.Equal(1001, qa.Attempts.Count); // Audit history was not changed.
    }

    [Fact]
    public void CompletedStageDropsSupersededWaitAndKeepsAnotherStagesCurrentWait()
    {
        var first = new WorkStageExecution { Id = Guid.NewGuid() };
        var second = new WorkStageExecution { Id = Guid.NewGuid() };
        Add(first, 1, "awaiting-compute", new { });
        Add(first, 2, "changes_requested", new { findings = "Required correction" });
        Add(second, 1, "awaiting-compute", new { operationId = Guid.NewGuid() });
        var history = first.Attempts.Concat(second.Attempts)
            .Select(x => JsonSerializer.Deserialize<WorkExecutionOutcomeV1>(x.ResultJson!, Json)!);
        Assert.Equal(new[] { "changes_requested", "awaiting-compute" },
            WorkExecutionResumeHistory.Compact(history).Select(x => x.OutcomeCode));
    }

    private static void Add(WorkStageExecution stage, int number, string code, object output)
    {
        var id = Guid.NewGuid();
        stage.Attempts.Add(new() { Id = id, StageExecutionId = stage.Id, Attempt = number,
            Status = WorkExecutionAttemptStatus.Completed,
            ResultJson = JsonSerializer.Serialize(new WorkExecutionOutcomeV1(stage.Id, id,
                WorkExecutionDispositions.Completed, code, "Observed result", JsonSerializer.SerializeToElement(output), [], []), Json) });
    }
}
