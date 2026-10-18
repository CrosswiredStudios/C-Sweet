using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Analytics;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed partial class ProjectHealthTests
{
    [Theory]
    [InlineData("personal")] [InlineData("unboarded")] [InlineData("attention")]
    [InlineData("failed-callback")] [InlineData("other-root")] [InlineData("other-employee")]
    [InlineData("unbound-attempt")] [InlineData("old-callback")] [InlineData("later-state-change")]
    [InlineData("production-task")] [InlineData("blocked")] [InlineData("no-wait")]
    public async Task PersonalTicketFailureClosesOnlyAfterItsAssignedEmployeeRecordsAHealthyWaitInASuccessfulCallback(string scenario)
    {
        await using var f = await Fixture.Create();
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = f.Org, Kind = WorkBoardKind.Personal,
            OwnerOrganizationUserId = f.Producer.Id, Name = "Personal" };
        var task = f.Ticket(WorkTaskStatus.Failed);
        task.BoardId = board.Id; task.AssignedEmployeeId = f.Producer.Id;
        task.PersonalWorkContextJson = JsonSerializer.Serialize(new { workstreamId = f.Project.Id });
        f.Db.AddRange(board, task); await f.Db.SaveChangesAsync(); await f.Review();
        var incident = Assert.Single(f.Db.ProjectIncidents.Where(i => i.Fingerprint == $"ticket:{task.Id:N}"));
        var retainedEvidence = incident.EvidenceJson;
        f.Clock.Now += TimeSpan.FromMinutes(1);
        task.Status = WorkTaskStatus.Running; task.BlockReason = null;
        task.WaitingReason = "Waiting for independent aggregate delivery acceptance.";
        task.NextReviewAt = f.Clock.Now.AddHours(1); task.UpdatedAt = f.Clock.Now;
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(),
            AgentInstallationId = f.Producer.AgentInstallationId!.Value, Kind = CSweet.Domain.Setup.AgentWorkKind.Event,
            Name = scenario == "attention" ? AgentAttentionEvents.ReviewDue : PersonalTodoEvents.Available,
            SourceType = "platform-event", Status = AgentWorkStatus.Completed, CreatedAt = f.Clock.Now,
            CompletedAt = f.Clock.Now, DeadlineAt = f.Clock.Now.AddHours(1), AttemptCount = 1 };
        var attempt = new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, Attempt = 1,
            ClaimedAt = f.Clock.Now, FinishedAt = f.Clock.Now, WorkSucceeded = true };
        var context = new WorkExecutionContext { Id = attempt.Id, OrganizationId = f.Org,
            AgentInstallationId = work.AgentInstallationId, AgentWorkItemId = work.Id, RootWorkItemId = task.Id };
        switch (scenario)
        {
            case "unboarded": task.BoardId = null; break;
            case "failed-callback": attempt.WorkSucceeded = false; break;
            case "other-root": context.RootWorkItemId = Guid.NewGuid(); break;
            case "other-employee": task.AssignedEmployeeId = f.Director.Id; break;
            case "unbound-attempt": context.Id = Guid.NewGuid(); break;
            case "old-callback": attempt.ClaimedAt -= TimeSpan.FromMinutes(2); break;
            case "later-state-change": task.UpdatedAt += TimeSpan.FromSeconds(1); break;
            case "production-task": task.BoardId = f.Board.Id; break;
            case "blocked": task.Status = WorkTaskStatus.Blocked; task.BlockReason = "Still blocked"; break;
            case "no-wait": task.WaitingReason = null; break;
        }
        f.Db.AddRange(work, attempt, context); await f.Db.SaveChangesAsync(); await f.Review();
        Assert.Equal(scenario is "personal" or "unboarded" or "attention" ? "Resolved" : "Open", incident.Status);
        Assert.Contains("agent.payload_invalid", retainedEvidence);
        Assert.Contains("agent.payload_invalid", incident.EvidenceJson);
        Assert.NotEqual(WorkTaskStatus.Completed, task.Status);
    }
}
