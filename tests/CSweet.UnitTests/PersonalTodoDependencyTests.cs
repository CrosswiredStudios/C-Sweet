using System.Text.Json;
using CSweet.Application.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed partial class PersonalTodoServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Referenced_decision_wakes_work_even_when_event_is_missed_or_decision_precedes_defer(bool decidedBeforeDefer)
    {
        await using var db=CreateDb(); var setup=Seed(db); await db.SaveChangesAsync();
        var service=new PersonalTodoService(db,TimeProvider.System);
        var owner=new PersonalTodoActor(setup.Agent.Id,setup.Agent.AgentInstallationId);
        var project=Guid.NewGuid(); var team=Guid.NewGuid();
        SeedDependencyScope(db,setup,team,project); await db.SaveChangesAsync();
        var item=await service.AddAsync(setup.Organization.Id,owner,Add("Await independent review","dependency-work",null) with {
            WorkContext=new(WorkstreamId:project,TeamId:team) });
        var unaffected=await service.AddAsync(setup.Organization.Id,owner,Add("Independent draft","unaffected-draft",null));
        var record=new ResourceChangeRequestRecord { Id=Guid.NewGuid(),OrganizationId=setup.Organization.Id,
            RequesterOrganizationUserId=setup.Agent.Id,RequesterInstallationId=setup.Agent.AgentInstallationId!.Value,
            ManagerOrganizationUserId=setup.FirstManager.Id,TeamId=team,WorkstreamId=project,
            Status=decidedBeforeDefer?ResourceChangeRequestStatus.RevisionRequested:ResourceChangeRequestStatus.Pending };
        db.ResourceChangeRequests.Add(record);
        var stored=await db.CoreWorkTasks.SingleAsync(x=>x.Id==item.Id);
        var eventId=Guid.NewGuid(); stored.Status=WorkTaskStatus.Running; stored.ClaimEventId=eventId;
        stored.ClaimExpiresAt=DateTimeOffset.UtcNow.AddMinutes(5); stored.Revision++;
        await db.SaveChangesAsync();
        var dependency=new Wire.PersonalTodoDependency(Wire.PersonalTodoDependencyKinds.ResourceChange,record.Id,"Pending");
        var deferred=await service.DeferAsync(setup.Organization.Id,owner,new(item.Id,eventId,stored.Revision,
            DateTimeOffset.UtcNow.AddHours(1),"A differently worded explanation",setup.FirstManager.Id,"defer-exact-decision") { Dependency=dependency });
        if(!decidedBeforeDefer)
        {
            Assert.Equal(dependency,deferred.Wait!.Dependency);
            record.Status=ResourceChangeRequestStatus.RevisionRequested;
            await db.SaveChangesAsync(); // No decision event is delivered.
            db.ChangeTracker.Clear();
            await service.ReconcileAsync();
        }
        var ready=await db.CoreWorkTasks.SingleAsync(x=>x.Id==item.Id);
        Assert.Equal(WorkTaskStatus.Ready,ready.Status); Assert.Null(ready.NextReviewAt); Assert.Null(ready.WaitingDependencyJson);
        var revision=ready.Revision;
        var wakes=await db.AgentPlatformEventOutbox.CountAsync(x=>x.EventType==Wire.PersonalTodoEvents.Available);
        for(var i=0;i<3;i++) await service.ReconcileAsync(); // Duplicate/out-of-order wake hints cannot repeat the transition.
        Assert.Equal(revision,(await db.CoreWorkTasks.SingleAsync(x=>x.Id==item.Id)).Revision);
        Assert.Equal(wakes,await db.AgentPlatformEventOutbox.CountAsync(x=>x.EventType==Wire.PersonalTodoEvents.Available));
        Assert.Equal(WorkTaskStatus.Ready,(await db.CoreWorkTasks.SingleAsync(x=>x.Id==unaffected.Id)).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dependency_cannot_bind_another_owners_request_or_another_project(bool otherOwner)
    {
        await using var db=CreateDb(); var setup=Seed(db); await db.SaveChangesAsync();
        var service=new PersonalTodoService(db,TimeProvider.System); var actor=new PersonalTodoActor(setup.Agent.Id,setup.Agent.AgentInstallationId);
        var project=Guid.NewGuid(); var team=Guid.NewGuid();
        SeedDependencyScope(db,setup,team,project); await db.SaveChangesAsync();
        var item=await service.AddAsync(setup.Organization.Id,actor,Add("Await review","scoped-dependency",null) with {WorkContext=new(WorkstreamId:project,TeamId:team)});
        var resource=new ResourceChangeRequestRecord { Id=Guid.NewGuid(), OrganizationId=setup.Organization.Id,
            RequesterOrganizationUserId=otherOwner?setup.UnrelatedAgent.Id:setup.Agent.Id,
            RequesterInstallationId=(otherOwner?setup.UnrelatedAgent.AgentInstallationId:setup.Agent.AgentInstallationId)!.Value,
            TeamId=team,WorkstreamId=otherOwner?project:Guid.NewGuid() };
        db.ResourceChangeRequests.Add(resource);
        var stored=await db.CoreWorkTasks.SingleAsync(x=>x.Id==item.Id); var eventId=Guid.NewGuid();
        stored.Status=WorkTaskStatus.Running;stored.ClaimEventId=eventId;stored.ClaimExpiresAt=DateTimeOffset.UtcNow.AddMinutes(5);stored.Revision++;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.DeferAsync(setup.Organization.Id,actor,
            new(item.Id,eventId,stored.Revision,DateTimeOffset.UtcNow.AddMinutes(15),"Wait",null,"foreign-decision") {
                Dependency=new(Wire.PersonalTodoDependencyKinds.ResourceChange,resource.Id,"Pending") }));
        Assert.NotNull(stored.ClaimEventId); Assert.Null(stored.WaitingDependencyJson);
    }

    private static void SeedDependencyScope(CSweet.Infrastructure.Persistence.CSweetDbContext db, Setup setup, Guid team, Guid project)
    {
        db.OrganizationTeams.Add(new OrganizationTeam {Id=team,OrganizationId=setup.Organization.Id,TeamKey="research",
            Name="Research",NormalizedName="RESEARCH",LeadOrganizationUserId=setup.FirstManager.Id});
        db.Workstreams.Add(new Workstream {Id=project,OrganizationId=setup.Organization.Id,Name="Investigation",
            Outcome="Produce bounded evidence",AccountableManagerOrganizationUserId=setup.Agent.Id});
    }
}
