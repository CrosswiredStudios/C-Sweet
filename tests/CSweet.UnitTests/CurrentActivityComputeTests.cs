using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Contracts.Realtime;
using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed partial class CurrentActivityTests
{
    [Theory]
    [InlineData("Pending", "Waiting", "Waiting for compute dispatch")]
    [InlineData("Dispatching", "Waiting", "Waiting for compute execution")]
    [InlineData("Reconciling", "Waiting", "Waiting for compute to confirm")]
    [InlineData("Completed", "Waiting", "Compute operation completed")]
    [InlineData("Failed", "Needs attention", "Compute needs recovery")]
    public async Task ComputeWaitRemainsVisibleAfterCallbackCompletesWithoutCreatingPersonalWork(string status, string state, string action)
    {
        await using var fixture = new Fixture(); await fixture.SeedAsync();
        var (stage, operation) = await ComputeWaitAsync(fixture, status);
        var tasks = await fixture.Db.CoreWorkTasks.CountAsync();
        var boards = await fixture.Db.WorkBoards.CountAsync();
        var row = Assert.Single((await fixture.Service.ReadAsync(fixture.Org, fixture.User)).Items);
        Assert.Equal(state, row.State); Assert.StartsWith(action, row.CurrentAction);
        Assert.Equal(fixture.Ticket.Id, row.WorkItemId);
        Assert.Equal(fixture.Agent.Id, row.EmployeeId);
        Assert.Null(row.AgentWorkItemId); Assert.Null(row.AttemptId); Assert.Null(row.LeaseExpiresAt);
        Assert.Equal("Browser test preparation or transfer", row.Compute!.Phase);
        Assert.Equal(status == "Completed" ? 1 : 0, row.Compute.RecentCompletedOperations);
        Assert.Equal(operation.CompletedAt ?? operation.CreatedAt, row.LastProgressAt);
        Assert.Equal(stage.RetryAt, row.Compute.ResumeAt);
        Assert.DoesNotContain("secret-upload-bytes", JsonSerializer.Serialize(row));
        Assert.Equal(tasks, await fixture.Db.CoreWorkTasks.CountAsync());
        Assert.Equal(boards, await fixture.Db.WorkBoards.CountAsync());
    }

    [Theory]
    [InlineData("wrong-owner")]
    [InlineData("wrong-project")]
    [InlineData("wrong-org")]
    [InlineData("wrong-source")]
    [InlineData("old-assignment")]
    [InlineData("wrong-attempt")]
    [InlineData("malformed")]
    [InlineData("superseded")]
    [InlineData("archived")]
    [InlineData("denied")]
    public async Task ComputeProgressRequiresTheCurrentAuthorizedExactAssignment(string condition)
    {
        await using var fixture = new Fixture(); await fixture.SeedAsync();
        var (stage, operation) = await ComputeWaitAsync(fixture, "Completed");
        var environment = await fixture.Db.ComputeEnvironments.SingleAsync();
        var attempt = await fixture.Db.WorkExecutionAttempts.SingleAsync();
        switch (condition)
        {
            case "wrong-owner": operation.InstallationId = Guid.NewGuid(); break;
            case "wrong-project": environment.WorkstreamId = Guid.NewGuid(); break;
            case "wrong-org": environment.OrganizationId = Guid.NewGuid(); break;
            case "wrong-source": (await fixture.Db.SourceControlWorkspaces.SingleAsync()).BaseCommitSha = new('b', 40); break;
            case "old-assignment": fixture.Ticket.AssignmentRevision++; break;
            case "wrong-attempt": attempt.ResultJson = attempt.ResultJson!.Replace(attempt.Id.ToString(), Guid.NewGuid().ToString()); break;
            case "malformed": attempt.ResultJson = "{broken"; break;
            case "superseded": stage.Status = WorkStageExecutionStatus.Completed; break;
            case "archived": fixture.Ticket.ArchivedAt = DateTimeOffset.UtcNow; break;
            case "denied": fixture.Grants.Allow = false; break;
        }
        await fixture.Db.SaveChangesAsync();
        var rows = (await fixture.Service.ReadAsync(fixture.Org, fixture.User)).Items;
        if (condition is "archived" or "denied" or "superseded") Assert.Empty(rows);
        else
        {
            var row = Assert.Single(rows);
            Assert.Null(row.Compute);
            Assert.Equal("Waiting", row.State);
            Assert.Contains("progress details are unavailable", row.CurrentAction);
        }
    }

    [Fact]
    public async Task BrowserPhaseAndReceiptCountersDoNotImplyAVerdictOrCountDuplicateWakes()
    {
        await using var fixture = new Fixture(); await fixture.SeedAsync();
        var (stage, operation) = await ComputeWaitAsync(fixture, "Completed");
        operation.WorkloadJson = JsonSerializer.Serialize(new ComputeWorkload(BrowserTest:
            new(Guid.NewGuid(), new('a', 40), new('c', 64), [375, 1280])), ComputeProtocol.Json);
        var first = await fixture.Db.WorkExecutionAttempts.SingleAsync();
        var duplicate = new WorkExecutionAttempt { Id = Guid.NewGuid(), StageExecutionId = stage.Id, Attempt = 2,
            Status = WorkExecutionAttemptStatus.Completed, CompletedAt = DateTimeOffset.UtcNow,
            ResultJson = first.ResultJson!.Replace(first.Id.ToString(), "REPLACEMENT") };
        duplicate.ResultJson = duplicate.ResultJson.Replace("REPLACEMENT", duplicate.Id.ToString());
        fixture.Db.Add(duplicate); await fixture.Db.SaveChangesAsync();
        var row = Assert.Single((await fixture.Service.ReadAsync(fixture.Org, fixture.User)).Items);
        Assert.Equal("Browser testing", row.Compute!.Phase);
        Assert.Equal(1, row.Compute.RecentCompletedOperations);
        Assert.Equal("Waiting", row.State);
        operation.ResultJson = JsonSerializer.Serialize(new ComputeWorkloadResult(ErrorCode: "outcome-unknown"), ComputeProtocol.Json);
        await fixture.Db.SaveChangesAsync();
        row = Assert.Single((await fixture.Service.ReadAsync(fixture.Org, fixture.User)).Items);
        Assert.Equal("Needs attention", row.State);
        Assert.Equal(0, row.Compute!.RecentCompletedOperations);
    }

    [Fact]
    public async Task ChangedComputeGenerationShowsRecoveryInsteadOfContinuingOldWork()
    {
        await using var fixture = new Fixture(); await fixture.SeedAsync();
        await ComputeWaitAsync(fixture, "Completed");
        (await fixture.Db.ComputeEnvironments.SingleAsync()).Generation++;
        await fixture.Db.SaveChangesAsync();
        var row = Assert.Single((await fixture.Service.ReadAsync(fixture.Org, fixture.User)).Items);
        Assert.Equal("Needs attention", row.State);
        Assert.Contains("recovery", row.CurrentAction);
    }

    [Fact]
    public async Task PausedAndOverdueComputeWaitsAreNotExecutingOrFakeExpiredLeases()
    {
        await using var fixture = new Fixture(); await fixture.SeedAsync();
        var (stage, _) = await ComputeWaitAsync(fixture, "Completed");
        stage.RetryAt = DateTimeOffset.UtcNow.AddMinutes(-15);
        await fixture.Db.SaveChangesAsync();
        var row = Assert.Single((await fixture.Service.ReadAsync(fixture.Org, fixture.User)).Items);
        Assert.Equal("Waiting", row.State);
        Assert.Contains("awaiting the next workflow dispatch", row.CurrentAction);
        (await fixture.Db.WorkSprintExecutions.SingleAsync()).Status = WorkSprintExecutionStatus.Paused;
        await fixture.Db.SaveChangesAsync();
        row = Assert.Single((await fixture.Service.ReadAsync(fixture.Org, fixture.User)).Items);
        Assert.Contains("paused", row.CurrentAction); Assert.Null(row.Compute!.ResumeAt);
    }

    [Fact]
    public async Task ComputeDetailsRequireDiagnosticsButTheAssignedWaitStaysVisible()
    {
        await using var fixture = new Fixture(); await fixture.SeedAsync();
        await ComputeWaitAsync(fixture, "Dispatching");
        fixture.Owner.PermissionLevel = OrganizationPermissionLevel.Contributor;
        await fixture.Db.SaveChangesAsync();
        var row = Assert.Single((await fixture.Service.ReadAsync(fixture.Org, fixture.User)).Items);
        Assert.Equal("Waiting", row.State); Assert.False(row.CanInspect); Assert.Null(row.Compute);
        Assert.Equal("Waiting for compute execution and its result; the agent will resume automatically", row.CurrentAction);
    }

    [Fact]
    public async Task ComputeCardSurvivesQueuedResumeAndHandsOffToTheActualClaim()
    {
        await using var fixture = new Fixture(); await fixture.SeedAsync();
        var (stage, _) = await ComputeWaitAsync(fixture, "Completed");
        var work = await fixture.Db.AgentWorkItems.SingleAsync();
        work.Status = CSweet.Domain.Setup.AgentWorkStatus.Pending; work.AttemptCount = 2;
        stage.Status = WorkStageExecutionStatus.Dispatching;
        await fixture.Db.SaveChangesAsync();
        var waiting = Assert.Single((await fixture.Service.ReadAsync(fixture.Org, fixture.User)).Items);
        Assert.NotNull(waiting.Compute); Assert.Equal("Waiting", waiting.State);
        stage.Status = WorkStageExecutionStatus.Running;
        work.Status = CSweet.Domain.Setup.AgentWorkStatus.Leased;
        fixture.Db.Add(new CSweet.Domain.Setup.AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = work.Id,
            Attempt = 2, ClaimedAt = DateTimeOffset.UtcNow, LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(3) });
        await fixture.Db.SaveChangesAsync();
        var running = Assert.Single((await fixture.Service.ReadAsync(fixture.Org, fixture.User)).Items);
        Assert.Equal(waiting.Key, running.Key);
        Assert.Null(running.Compute); Assert.Equal("Executing", running.State);
    }

    [Fact]
    public async Task AcceptedComputeWaitEmitsAnImmediateContentFreeActivityHint()
    {
        await using var fixture = new Fixture(); await fixture.SeedAsync();
        var (stage, _) = await ComputeWaitAsync(fixture, "Pending");
        var before = await fixture.Db.ApplicationRealtimeOutbox.CountAsync(x => x.EventType == AppRealtimeEvents.CurrentActivityChanged);
        fixture.Db.Add(new WorkOrchestrationEvent { Id = Guid.NewGuid(), OrganizationId = fixture.Org,
            BoardId = fixture.Ticket.BoardId!.Value, SprintExecutionId = stage.ItemExecution!.SprintExecutionId,
            ItemExecutionId = stage.ItemExecutionId, StageExecutionId = stage.Id,
            EventType = "qa.compute.waiting", OccurredAt = DateTimeOffset.UtcNow });
        await fixture.Db.SaveChangesAsync();
        var hints = await fixture.Db.ApplicationRealtimeOutbox.Where(x => x.EventType == AppRealtimeEvents.CurrentActivityChanged).ToListAsync();
        Assert.Equal(before + 1, hints.Count);
        Assert.All(hints, hint => Assert.DoesNotContain("secret-upload-bytes", hint.DataJson));
    }

    [Fact]
    public async Task GenericSpecialistComputeUsesTheSameProjectionAndOperationChangesWakeReaders()
    {
        await using var fixture = new Fixture(); await fixture.SeedAsync();
        var (stage, operation) = await ComputeWaitAsync(fixture, "Pending");
        stage.StageKey = "specialist-execution";
        stage.ItemExecution!.CurrentStageKey = stage.StageKey;
        await fixture.Db.SaveChangesAsync();
        var previous = await fixture.Db.ApplicationRealtimeOutbox.CountAsync(x => x.EventType == AppRealtimeEvents.ComputeChanged);
        operation.Status = "Dispatching"; await fixture.Db.SaveChangesAsync();
        Assert.Equal(previous + 1, await fixture.Db.ApplicationRealtimeOutbox.CountAsync(x => x.EventType == AppRealtimeEvents.ComputeChanged));
        var row = Assert.Single((await fixture.Service.ReadAsync(fixture.Org, fixture.User)).Items);
        Assert.Equal("Compute workload", row.Compute!.Phase);
        Assert.Equal("Waiting", row.State);
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(previous + 1, await fixture.Db.ApplicationRealtimeOutbox.CountAsync(x => x.EventType == AppRealtimeEvents.ComputeChanged));
    }

    private static async Task<(WorkStageExecution Stage, ComputeOperation Operation)> ComputeWaitAsync(Fixture fixture, string status)
    {
        var now = DateTimeOffset.UtcNow;
        var attempt = await PendingResultAsync(fixture, 20);
        attempt.Attempt = 1; attempt.Status = WorkExecutionAttemptStatus.Completed; attempt.CompletedAt = now.AddSeconds(-10);
        var stage = await fixture.Db.WorkStageExecutions.SingleAsync();
        stage.Status = WorkStageExecutionStatus.Backoff; stage.LastOutcomeCode = "awaiting-compute";
        stage.RetryAt = now.AddMinutes(5); stage.UpdatedAt = now.AddSeconds(-10);
        stage.ItemExecution!.Status = WorkItemExecutionStatus.Pending;
        fixture.Ticket.Status = WorkTaskStatus.Ready;
        fixture.Ticket.AssignedEmployeeId = Guid.NewGuid(); fixture.Ticket.AssignedAgentInstallationId = Guid.NewGuid();
        var board = await fixture.Db.WorkBoards.SingleAsync();
        var environment = new ComputeEnvironment { Id = Guid.NewGuid(), OrganizationId = fixture.Org,
            InstallationId = fixture.Agent.AgentInstallationId!.Value, WorkstreamId = board.WorkstreamId!.Value,
            State = ComputeLifecycleState.Ready, Generation = 4, ProviderId = "fixture", ProviderNodeId = Guid.NewGuid() };
        var operation = new ComputeOperation { Id = Guid.NewGuid(), OrganizationId = fixture.Org,
            InstallationId = environment.InstallationId, EnvironmentId = environment.Id, Generation = 4,
            Status = status, CreatedAt = now.AddSeconds(-20), CompletedAt = status == "Completed" ? now : null,
            WorkloadJson = "{\"command\":{\"arguments\":[\"secret-upload-bytes\"]}}",
            ResultJson = JsonSerializer.Serialize(new ComputeWorkloadResult(Command:
                new(1, Guid.NewGuid(), Guid.NewGuid(), 0, false, [], [], false)), ComputeProtocol.Json) };
        attempt.ResultJson = JsonSerializer.Serialize(new Wire.WorkExecutionOutcomeV1(stage.Id, attempt.Id,
            Wire.WorkExecutionDispositions.Completed, "awaiting-compute", "Waiting", JsonSerializer.SerializeToElement(new {
                sourceCommitSha = new string('a', 40), buildSha256 = new string('c', 64), environmentId = environment.Id,
                operationIds = new[] { operation.Id }, retryAt = stage.RetryAt }, ComputeProtocol.Json), [], []), ComputeProtocol.Json);
        fixture.Db.AddRange(environment, operation, new SourceControlWorkspace { Id = Guid.NewGuid(), OrganizationId = fixture.Org,
            AgentInstallationId = environment.InstallationId, WorkItemId = fixture.Ticket.Id,
            AssignmentRevision = fixture.Ticket.AssignmentRevision, BaseCommitSha = new('a', 40) });
        await fixture.Db.SaveChangesAsync();
        return (stage, operation);
    }
}
