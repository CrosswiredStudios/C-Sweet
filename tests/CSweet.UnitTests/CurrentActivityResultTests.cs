using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class CurrentActivityTests
{
    [Theory]
    [InlineData(0, "Finalizing", "Agent finished; awaiting workflow result processing")]
    [InlineData(10, "Needs attention", "Agent finished; workflow result processing is delayed")]
    public async Task ReturnedResultIsNotMisreportedAsAnExpiredExecutionLease(int minutes, string state, string action)
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        var orchestrationAttempt = await PendingResultAsync(fixture, minutes);
        await fixture.ProgressAsync(fixture.Ticket.Id, "QA report completed");
        var row = Assert.Single((await fixture.Service.ReadAsync(fixture.Org, fixture.User)).Items);
        Assert.Equal(state, row.State);
        Assert.Equal(action, row.CurrentAction);
        Assert.Equal(fixture.Attempt.Id, row.AttemptId);
        Assert.Equal(fixture.Attempt.FinishedAt, row.LastProgressAt);
        Assert.Null(row.LeaseExpiresAt);
        Assert.Equal("Testing", row.ActivityKind);
        Assert.Contains((await fixture.Service.FeedAsync(fixture.Org, fixture.User, fixture.Agent.Id,
            fixture.Attempt.Id, fixture.Ticket.Id, 0, default)).Entries, entry => entry.Text == "QA report completed");
        orchestrationAttempt.Status = WorkExecutionAttemptStatus.Completed;
        await fixture.Db.SaveChangesAsync();
        Assert.DoesNotContain((await fixture.Service.ReadAsync(fixture.Org, fixture.User)).Items,
            item => item.AgentWorkItemId == fixture.Attempt.AgentWorkItemId);
    }

    [Fact]
    public async Task PendingResultStillRequiresBoardReadAuthority()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        await PendingResultAsync(fixture, 10);
        fixture.Grants.Allow = false;
        Assert.Empty((await fixture.Service.ReadAsync(fixture.Org, fixture.User)).Items);
    }

    [Fact]
    public async Task CompletedUnrelatedWorkIsNotRediscoveredAsPendingResult()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        var work = await fixture.Db.AgentWorkItems.SingleAsync();
        work.Status = AgentWorkStatus.Completed;
        fixture.Attempt.FinishedAt = DateTimeOffset.UtcNow;
        await fixture.Db.SaveChangesAsync();
        Assert.DoesNotContain((await fixture.Service.ReadAsync(fixture.Org, fixture.User)).Items,
            item => item.AgentWorkItemId == work.Id);
    }

    private static async Task<WorkExecutionAttempt> PendingResultAsync(Fixture fixture, int minutes)
    {
        var board = await fixture.Db.WorkBoards.SingleAsync();
        var sprint = new WorkSprintExecution { Id = Guid.NewGuid(), OrganizationId = fixture.Org,
            BoardId = board.Id, Status = WorkSprintExecutionStatus.Active };
        var execution = new WorkItemExecution { Id = Guid.NewGuid(), SprintExecutionId = sprint.Id,
            WorkItemId = fixture.Ticket.Id, CurrentStageKey = "quality", Status = WorkItemExecutionStatus.Running };
        var stage = new WorkStageExecution { Id = Guid.NewGuid(), ItemExecutionId = execution.Id,
            StageKey = "quality", Status = WorkStageExecutionStatus.Running,
            OrganizationUserId = fixture.Agent.Id, AgentInstallationId = fixture.Agent.AgentInstallationId };
        var attempt = new WorkExecutionAttempt { Id = Guid.NewGuid(), StageExecutionId = stage.Id,
            AgentWorkItemId = fixture.Attempt.AgentWorkItemId, Status = WorkExecutionAttemptStatus.Running };
        var work = await fixture.Db.AgentWorkItems.SingleAsync();
        work.Status = AgentWorkStatus.Completed;
        work.SourceType = "WorkStageExecution";
        work.SourceId = stage.Id.ToString();
        work.CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-minutes);
        fixture.Attempt.FinishedAt = work.CompletedAt;
        fixture.Attempt.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        fixture.Db.AddRange(sprint, execution, stage, attempt);
        await fixture.Db.SaveChangesAsync();
        return attempt;
    }
}
