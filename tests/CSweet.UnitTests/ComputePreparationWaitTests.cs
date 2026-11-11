using System.Text.Json;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

/// <summary>
/// Super Awesome Games (2026-11-10): QA failed Prism Break #20 back to engineering three times because the project's
/// browser compute was not prepared. QA now reports a bounded wait, which must not consume a retry or reach a verdict.
/// </summary>
public sealed class ComputePreparationWaitTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Setup(CSweetDbContext Db, WorkOrchestrator Orchestrator, WorkSprintExecution Sprint,
        WorkStageExecution Stage, WorkExecutionAttempt Attempt);

    private static Setup Create(string stageKey = "quality")
    {
        var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var sprint = new WorkSprintExecution { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), BoardId = Guid.NewGuid() };
        var item = new WorkItemExecution { Id = Guid.NewGuid(), SprintExecutionId = sprint.Id, Status = WorkItemExecutionStatus.Running };
        var stage = new WorkStageExecution { Id = Guid.NewGuid(), ItemExecutionId = item.Id, ItemExecution = item, StageKey = stageKey,
            AgentInstallationId = Guid.NewGuid(), Status = WorkStageExecutionStatus.Running };
        var attempt = new WorkExecutionAttempt { Id = Guid.NewGuid(), StageExecutionId = stage.Id, Attempt = 1, Status = WorkExecutionAttemptStatus.Running };
        stage.Attempts.Add(attempt);
        return new(db, new WorkOrchestrator(db, null!, null!, null!, [], TimeProvider.System, null!), sprint, stage, attempt);
    }

    private static WorkExecutionOutcomeV1 Wait(Setup s, DateTimeOffset retryAt, string reason = "Browser compute preparation is not ready (Failed; compute_checks_failed).") =>
        new(s.Stage.Id, s.Attempt.Id, WorkExecutionDispositions.Completed, WorkOrchestrator.ComputePreparationWait,
            "Waiting for browser compute: " + reason, JsonSerializer.SerializeToElement(new { reason, retryAt }, Json), [], []);

    [Fact]
    public void A_valid_wait_backs_off_without_a_verdict_or_a_consumed_retry()
    {
        var s = Create();
        var now = DateTimeOffset.UtcNow;

        var error = s.Orchestrator.AcceptComputePreparationWait(s.Sprint, s.Stage, s.Attempt, Wait(s, now.AddMinutes(10)), now);

        Assert.Null(error);
        Assert.Equal(WorkStageExecutionStatus.Backoff, s.Stage.Status);
        Assert.Equal(WorkOrchestrator.ComputePreparationWait, s.Stage.LastOutcomeCode);
        Assert.Null(s.Stage.LastError);
        Assert.Equal(now.AddMinutes(10), s.Stage.RetryAt);
        Assert.Contains("compute_checks_failed", s.Stage.LastSummary);
        Assert.Equal(WorkItemExecutionStatus.Pending, s.Stage.ItemExecution!.Status);
        Assert.Equal(WorkExecutionAttemptStatus.Completed, s.Attempt.Status);
        Assert.Equal(0, WorkOrchestrator.RetryAttemptCount(s.Stage));
        Assert.Single(s.Db.ChangeTracker.Entries<WorkOrchestrationEvent>());
    }

    [Theory]
    [InlineData("technical-review", 10, "Not ready")]
    [InlineData("quality", 60, "Not ready")]
    [InlineData("quality", -1, "Not ready")]
    [InlineData("quality", 10, " ")]
    public void Waits_are_limited_to_qa_with_a_reason_and_a_bounded_retry(string stageKey, int minutes, string reason)
    {
        var s = Create(stageKey);
        var now = DateTimeOffset.UtcNow;

        Assert.NotNull(s.Orchestrator.AcceptComputePreparationWait(s.Sprint, s.Stage, s.Attempt, Wait(s, now.AddMinutes(minutes), reason), now));
        Assert.NotEqual(WorkStageExecutionStatus.Backoff, s.Stage.Status);
    }

    [Fact]
    public void Both_compute_waits_are_excluded_from_retry_counting()
    {
        Assert.True(WorkOrchestrator.IsComputeWait("awaiting-compute"));
        Assert.True(WorkOrchestrator.IsComputeWait("awaiting-compute-preparation"));
        Assert.False(WorkOrchestrator.IsComputeWait("failed"));
    }

    [Fact]
    public void Malformed_verification_declarations_are_rejected()
    {
        CSweet.AgentHost.Broker.WorkManagementCapabilityHandler.ValidateDeclaredVerifications(null);
        CSweet.AgentHost.Broker.WorkManagementCapabilityHandler.ValidateDeclaredVerifications(new(["Scene"], ["Renders"]) { Verifications = [] });
        Assert.Throws<ArgumentException>(() => CSweet.AgentHost.Broker.WorkManagementCapabilityHandler.ValidateDeclaredVerifications(
            new(["Scene"], ["Renders"]) { Verifications = [new("pixel-diff")] }));
    }
}
