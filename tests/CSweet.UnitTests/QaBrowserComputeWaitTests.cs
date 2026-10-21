using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Shared = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class QaBrowserComputeWaitTests
{
    [Fact]
    public async Task Waiting_qa_reserves_its_own_capacity_so_another_ticket_cannot_mutate_its_compute_generation()
    {
        await using var database = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var installation = Guid.NewGuid();
        database.AgentInstallations.Add(new() { Id = installation, PackageVersion = new() {
            Id = Guid.NewGuid(), ManifestJson = "{\"runtime\":{\"maximumConcurrentJobs\":1}}",
            PackageSource = new() { Id = Guid.NewGuid(), RepositoryUrl = "https://example.invalid/browser-fixture" } } });
        var waiting = new WorkStageExecution { Id = Guid.NewGuid(), StageKey = "quality", AgentInstallationId = installation,
            LastOutcomeCode = "awaiting-compute", Status = WorkStageExecutionStatus.Backoff };
        database.WorkStageExecutions.Add(waiting); await database.SaveChangesAsync();
        var policy = new WorkOrchestrationPolicyRevision(); policy.Stages.Add(new() { Key = "quality" });
        var orchestrator = new WorkOrchestrator(database, null!, null!, null!, [], TimeProvider.System, null!);
        Assert.False(await orchestrator.HasCapacityAsync(new(), policy, new() {
            Id = Guid.NewGuid(), StageKey = "quality", AgentInstallationId = installation }, default));
        waiting.Status = WorkStageExecutionStatus.Pending; await database.SaveChangesAsync();
        Assert.True(await orchestrator.HasCapacityAsync(new(), policy, waiting, default));
    }

    [Fact]
    public void Many_compute_wakes_do_not_exhaust_the_failure_retry_budget()
    {
        var stage = new WorkStageExecution();
        for (var attempt = 0; attempt < 100; attempt++)
            stage.Attempts.Add(new() { Status = WorkExecutionAttemptStatus.Completed,
                ResultJson = JsonSerializer.Serialize(new Shared.WorkExecutionOutcomeV1(Guid.NewGuid(), Guid.NewGuid(),
                    Shared.WorkExecutionDispositions.Completed, "awaiting-compute", "Waiting", JsonSerializer.SerializeToElement(new { }), [], []), ComputeProtocol.Json) });
        stage.Attempts.Add(new() { Status = WorkExecutionAttemptStatus.Failed });
        Assert.Equal(1, WorkOrchestrator.RetryAttemptCount(stage));
        stage.Attempts.Add(new() { Status = WorkExecutionAttemptStatus.Completed, ResultJson = "malformed" });
        Assert.Equal(2, WorkOrchestrator.RetryAttemptCount(stage));
    }

    [Theory]
    [InlineData("valid", true)] [InlineData("wrong-owner", false)] [InlineData("wrong-source", false)]
    [InlineData("stale-generation", false)] [InlineData("wrong-project", false)] [InlineData("unbounded", false)]
    public async Task Compute_waits_preserve_quality_without_granting_a_verdict(string condition, bool accepted)
    {
        await using var database = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var now = DateTimeOffset.UtcNow;
        var organization = Guid.NewGuid(); var installation = Guid.NewGuid(); var project = Guid.NewGuid();
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = organization, WorkstreamId = project };
        var item = new WorkTask { Id = Guid.NewGuid(), OrganizationId = organization, AssignmentRevision = 3 };
        var execution = new WorkSprintExecution { Id = Guid.NewGuid(), OrganizationId = organization, BoardId = board.Id };
        var itemExecution = new WorkItemExecution { Id = Guid.NewGuid(), WorkItem = item, WorkItemId = item.Id, SprintExecution = execution };
        var stage = new WorkStageExecution { Id = Guid.NewGuid(), StageKey = "quality", AgentInstallationId = installation,
            ItemExecution = itemExecution, ItemExecutionId = itemExecution.Id, Status = WorkStageExecutionStatus.Running };
        var attempt = new WorkExecutionAttempt { Id = Guid.NewGuid(), StageExecutionId = stage.Id, Attempt = 1, Status = WorkExecutionAttemptStatus.Running };
        var environment = new ComputeEnvironment { Id = Guid.NewGuid(), OrganizationId = organization, InstallationId = installation,
            WorkstreamId = condition == "wrong-project" ? Guid.NewGuid() : project, State = ComputeLifecycleState.Busy, Generation = 4,
            ProviderId = "test-provider", ProviderNodeId = Guid.NewGuid() };
        var operation = new ComputeOperation { Id = Guid.NewGuid(), OrganizationId = organization, EnvironmentId = environment.Id,
            InstallationId = condition == "wrong-owner" ? Guid.NewGuid() : installation, Generation = condition == "stale-generation" ? 3 : 4,
            Status = "Dispatching" };
        var workspace = new SourceControlWorkspace { Id = Guid.NewGuid(), OrganizationId = organization, AgentInstallationId = installation,
            WorkItemId = item.Id, AssignmentRevision = 3, BaseCommitSha = new('a', 40) };
        if (condition == "wrong-owner") environment.InstallationId = operation.InstallationId;
        database.AddRange(board, item, environment, operation, workspace); await database.SaveChangesAsync();
        var pending = new WorkOrchestrator.QaComputeWait(condition == "wrong-source" ? new('b', 40) : workspace.BaseCommitSha,
            new('c', 64), environment.Id, [operation.Id], now.AddMinutes(condition == "unbounded" ? 30 : 5));
        var outcome = new Shared.WorkExecutionOutcomeV1(stage.Id, attempt.Id, Shared.WorkExecutionDispositions.Completed,
            "awaiting-compute", "Browser testing continues", JsonSerializer.SerializeToElement(pending, ComputeProtocol.Json), [], []);
        var orchestrator = new WorkOrchestrator(database, null!, null!, null!, [], TimeProvider.System, null!);
        var error = await orchestrator.AcceptComputeWaitAsync(execution, stage, attempt, outcome, now, default);
        Assert.Equal(accepted, error is null);
        Assert.Empty(database.SourceControlValidations.Local);
        if (accepted)
        {
            Assert.Equal(WorkStageExecutionStatus.Backoff, stage.Status);
            Assert.Equal(WorkItemExecutionStatus.Pending, itemExecution.Status);
            Assert.Equal(pending.RetryAt, stage.RetryAt);
            Assert.Equal(WorkExecutionAttemptStatus.Completed, attempt.Status);
        }
        else Assert.Equal(WorkStageExecutionStatus.Running, stage.Status);
    }

    [Theory]
    [InlineData("valid", true)] [InlineData("wrong-build", false)] [InlineData("wrong-source", false)]
    [InlineData("failed-browser", false)] [InlineData("unknown", false)] [InlineData("wrong-owner", false)]
    public async Task Final_browser_evidence_requires_the_actual_exact_compute_receipt(string condition, bool accepted)
    {
        await using var database = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var organization = Guid.NewGuid(); var installation = Guid.NewGuid(); var commit = new string('a', 40);
        var browser = new ComputeBrowserTest(Guid.NewGuid(), condition == "wrong-source" ? new('b', 40) : commit, new('c', 64), [375, 1280]);
        var report = JsonSerializer.SerializeToUtf8Bytes(new { browser.RequestId, browser.SourceCommitSha, browser.BuildSha256,
            evidenceSha256 = new string('d', 64), passed = condition != "failed-browser" }, ComputeProtocol.Json);
        var operation = new ComputeOperation { Id = Guid.NewGuid(), OrganizationId = organization,
            InstallationId = condition == "wrong-owner" ? Guid.NewGuid() : installation, Status = "Completed",
            WorkloadJson = JsonSerializer.Serialize(new ComputeWorkload(BrowserTest: browser), ComputeProtocol.Json),
            ResultJson = JsonSerializer.Serialize(condition == "unknown" ? new ComputeWorkloadResult(ErrorCode: "outcome-unknown") :
                new ComputeWorkloadResult(Command: new(1, Guid.NewGuid(), browser.RequestId, 0, false, report, [], false)), ComputeProtocol.Json) };
        var environment = new ComputeEnvironment { Id = Guid.NewGuid(), OrganizationId = organization, InstallationId = operation.InstallationId,
            ProviderId = "test-provider", ProviderNodeId = Guid.NewGuid() };
        operation.EnvironmentId = environment.Id;
        database.AddRange(environment, operation); await database.SaveChangesAsync();
        var outcome = new Shared.WorkExecutionOutcomeV1(Guid.NewGuid(), Guid.NewGuid(), Shared.WorkExecutionDispositions.Completed,
            "passed", "Observed browser pass", JsonSerializer.SerializeToElement(new { }),
            [new("browser-compute-operation", "Test", operation.Id.ToString()),
             new("browser-build-sha256", "Build", condition == "wrong-build" ? new('e', 64) : browser.BuildSha256),
             new("browser-evidence-sha256", "Evidence", new('d', 64))], []);
        var orchestrator = new WorkOrchestrator(database, null!, null!, null!, [], TimeProvider.System, null!);
        var error = await orchestrator.ValidateBrowserEvidenceAsync(new() { OrganizationId = organization },
            new() { AgentInstallationId = installation }, outcome, commit, default);
        Assert.Equal(accepted, error is null);
    }
}
