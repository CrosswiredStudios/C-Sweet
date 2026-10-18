using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Analytics;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.WorkManagement.Contracts;
using AgentWorkKind = CSweet.Domain.Setup.AgentWorkKind;

namespace CSweet.UnitTests;

public sealed partial class ProjectHealthTests
{
    [Theory]
    [InlineData("personal-wake")] [InlineData("attention-wake")]
    [InlineData("legacy-unboarded")]
    [InlineData("no-success")] [InlineData("other-root")] [InlineData("other-installation")]
    [InlineData("older-success")] [InlineData("unbound-attempt")]
    [InlineData("blocked-personal")] [InlineData("board-task")] [InlineData("capability")]
    public async Task HistoricalWakeFailureRequiresExactSuccessfulPersonalCallbackBeforeItCanResolve(string scenario)
    {
        await using var f=await Fixture.Create();
        var personalBoard=new CSweet.Domain.WorkManagement.WorkBoard {Id=Guid.NewGuid(),OrganizationId=f.Org,
            Kind=CSweet.Domain.WorkManagement.WorkBoardKind.Personal,OwnerOrganizationUserId=f.Producer.Id,Name="Personal"};
        f.Db.Add(personalBoard);
        var task=f.Ticket(WorkTaskStatus.Running); task.BoardId=scenario=="legacy-unboarded"?null:personalBoard.Id; task.AssignedEmployeeId=f.Producer.Id;
        task.PersonalWorkContextJson=JsonSerializer.Serialize(new { workstreamId=f.Project.Id });
        task.WaitingReason="The current increment awaits independent aggregate delivery acceptance before the next Story can be refined.";
        task.NextReviewAt=f.Clock.Now.AddHours(1);
        var old=new AgentWorkItem { Id=Guid.NewGuid(),OrganizationId=f.Org.ToString(),AgentInstallationId=f.Producer.AgentInstallationId!.Value,
            Kind=AgentWorkKind.Event,Name=PersonalTodoEvents.Available,SourceType="platform-event",Status=AgentWorkStatus.DeadLetter,
            CreatedAt=f.Clock.Now,CompletedAt=f.Clock.Now,DeadlineAt=f.Clock.Now.AddHours(1),AttemptCount=1,LastError="Historical planning generator crashed" };
        var stopped=new AgentWorkAttempt { Id=Guid.NewGuid(),AgentWorkItemId=old.Id,Attempt=1,ClaimedAt=f.Clock.Now.AddMinutes(-1),
            FinishedAt=f.Clock.Now,WorkSucceeded=false,Error=old.LastError };
        f.Db.AddRange(task,old,stopped,new WorkExecutionContext { Id=stopped.Id,OrganizationId=f.Org,
            AgentInstallationId=old.AgentInstallationId,AgentWorkItemId=old.Id,RootWorkItemId=task.Id });
        await f.Db.SaveChangesAsync(); await f.Review();
        var incident=Assert.Single(f.Db.ProjectIncidents.Where(i=>i.Fingerprint==$"work:{old.Id:N}"));
        f.Clock.Now+=TimeSpan.FromMinutes(1);
        var later=new AgentWorkItem { Id=Guid.NewGuid(),OrganizationId=f.Org.ToString(),AgentInstallationId=old.AgentInstallationId,
            Kind=AgentWorkKind.Event,Name=scenario=="attention-wake"?AgentAttentionEvents.ReviewDue:PersonalTodoEvents.Available,
            SourceType="platform-event",Status=AgentWorkStatus.Completed,CreatedAt=f.Clock.Now,CompletedAt=f.Clock.Now,
            DeadlineAt=f.Clock.Now.AddHours(1),AttemptCount=1 };
        var success=new AgentWorkAttempt { Id=Guid.NewGuid(),AgentWorkItemId=later.Id,Attempt=1,ClaimedAt=f.Clock.Now,
            FinishedAt=f.Clock.Now,WorkSucceeded=true };
        var context=new WorkExecutionContext { Id=success.Id,OrganizationId=f.Org,AgentInstallationId=later.AgentInstallationId,
            AgentWorkItemId=later.Id,RootWorkItemId=task.Id };
        switch(scenario) {
            case "no-success": success.WorkSucceeded=false; break;
            case "other-root": context.RootWorkItemId=Guid.NewGuid(); break;
            case "other-installation": later.AgentInstallationId=Guid.NewGuid();context.AgentInstallationId=later.AgentInstallationId; break;
            case "older-success": success.ClaimedAt=f.Clock.Now.AddMinutes(-2);break;
            case "unbound-attempt": context.Id=Guid.NewGuid();break;
            case "blocked-personal": task.Status=WorkTaskStatus.Blocked;task.BlockReason="A real unresolved product constraint";break;
            case "board-task": task.BoardId=f.Board.Id;break;
            case "capability": old.Kind=AgentWorkKind.Capability;break;
        }
        f.Db.AddRange(later,success,context);await f.Db.SaveChangesAsync();await f.Review();
        Assert.Equal(scenario is "personal-wake" or "attention-wake" or "legacy-unboarded"?"Resolved":"Open",incident.Status);
        Assert.Equal(AgentWorkStatus.DeadLetter,old.Status);Assert.Equal("Historical planning generator crashed",old.LastError);
        if(scenario is "personal-wake" or "attention-wake" or "legacy-unboarded") {
            Assert.Equal(WorkTaskStatus.Running,task.Status);Assert.Contains("independent aggregate delivery acceptance",task.WaitingReason);
            Assert.Contains("Historical planning generator crashed",incident.EvidenceJson);
        }
    }
}
