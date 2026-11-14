using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class LifecycleReadinessTests
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("cancelled")]
    [InlineData("participant-removed")]
    [InlineData("grant-revoked")]
    public async Task DeliveryCannotAdvanceAfterItsProjectOrDelegatedAuthorityIsWithdrawn(string condition)
    {
        await using var f=await HierarchicalDeliveryServiceTests.Fixture.Create(true);
        await f.Activate(await f.Configure());await f.CompleteTasks();
        if(condition=="cancelled") (await f.Db.Workstreams.SingleAsync(p=>p.Id==f.Project)).Status=WorkstreamStatus.Cancelled;
        else if(condition=="participant-removed") (await f.Db.ProjectParticipants.SingleAsync(p=>p.WorkstreamId==f.Project&&p.OrganizationUserId==f.Manager.Id)).RemovedAt=DateTimeOffset.UtcNow;
        else foreach(var grant in await f.Db.ScopedActionGrants.Where(g=>g.SubjectId==f.Manager.Id&&g.Action==WorkDeliveryCapabilities.Control).ToListAsync())grant.RevokedAt=DateTimeOffset.UtcNow;
        await f.Db.SaveChangesAsync();await f.Service.PulseAsync();
        Assert.DoesNotContain(await f.Db.WorkDeliveryExecutions.ToListAsync(),e=>e.Status=="Completed");
        Assert.All(await f.Db.WorkDeliveryExecutions.ToListAsync(),e=>Assert.Equal("Blocked",e.Status));
    }

    [Fact]
    public async Task LegacyReconciliationRejectsAnInventedOriginalStaffingSnapshot()
    {
        await using var f=await HierarchicalDeliveryServiceTests.Fixture.Create(true);
        await f.Activate(await f.Configure());var before=await f.Read();var stored=await f.Db.WorkDeliveryPlans.SingleAsync();
        var original=before.Scopes.SelectMany(s=>s.ChildInputs??[]).First(i=>i.ItemId==f.Task.Id);
        stored.ScopesJson=JsonSerializer.Serialize(before.Scopes.Select(s=>s with {ChildInputs=null,ChildAssignments=null}),Json);
        var qa=await f.Db.WorkItemStageAssignments.SingleAsync(a=>a.WorkItemId==f.Task.Id&&a.StageKey=="quality");qa.OrganizationUserId=f.Technical.Id;
        f.Task.PlanningRevision++;await f.Db.SaveChangesAsync();
        var forged=original with {Stages=original.Stages.Select(a=>a.StageKey=="quality"?a with {OrganizationUserId=f.Technical.Id}:a).ToArray()};
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.ControlAsync(f.Org,f.Manager.Id,
            new(before.Id,before.Revision,"reconcile-inputs","reject-forged-history") {
                PlanningChanges=[new(f.Task.Id,f.Task.PlanningRevision-1,f.Task.PlanningRevision)],PreservedInputs=[forged]}));
        Assert.Contains("accepted delivery digest",error.Message);Assert.Equal(before.ScopeRevision,(await f.Read()).ScopeRevision);
    }

    [Fact]
    public async Task ReleaseDependenciesRequireAcceptedCurrentEvidenceAndSurviveCancellation()
    {
        await using var f=await HierarchicalDeliveryServiceTests.Fixture.Create(false);
        var request=f.Request();request=request with {TicketWorkflows=request.TicketWorkflows.Select(w=>w.Scope=="Release"?
            w with {FinalReleaseDependencyItemIds=[f.Task.Id]}:w).ToArray()};
        await f.Activate(await f.Configure(request));
        var plan=await f.Db.WorkDeliveryPlans.SingleAsync();
        f.Task.Status=WorkTaskStatus.Completed;await f.Db.SaveChangesAsync();
        Assert.False(await f.Service.ReleaseObligationsCompleteAsync(plan,default)); // Manual Done is insufficient.
        await f.CompleteTasks();await f.Service.PulseAsync();
        await f.Approve((await f.Read()).Executions.Single(e=>e.Scope=="Story"));await f.Service.PulseAsync();
        Assert.True(await f.Service.ReleaseObligationsCompleteAsync(plan,default));
        f.Task.PlanningRevision++;await f.Db.SaveChangesAsync();Assert.False(await f.Service.ReleaseObligationsCompleteAsync(plan,default));
        f.Task.PlanningRevision--;plan.Status="Cancelled";await f.Db.SaveChangesAsync();
        Assert.False(await f.Service.ReleaseObligationsCompleteAsync(plan,default));
    }

    [Fact]
    public async Task ApprovedInputReconciliationCannotOverwriteAnActiveTaskWriter()
    {
        await using var f=await HierarchicalDeliveryServiceTests.Fixture.Create(true);
        await f.Activate(await f.Configure());var prior=await f.Read();f.Task.PlanningRevision++;
        var execution=new WorkItemExecution {Id=Guid.NewGuid(),WorkItemId=f.Task.Id,Status=WorkItemExecutionStatus.Running};
        var stage=new WorkStageExecution {Id=Guid.NewGuid(),StageKey="development",Status=WorkStageExecutionStatus.Running};
        stage.Attempts.Add(new(){Id=Guid.NewGuid(),Status=WorkExecutionAttemptStatus.Running});execution.Stages.Add(stage);
        f.Db.WorkItemExecutions.Add(execution);await f.Db.SaveChangesAsync();
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.ControlAsync(f.Org,f.Manager.Id,
            new(prior.Id,prior.Revision,"reconcile-inputs","writer-fence") {PlanningChanges=[new(f.Task.Id,f.Task.PlanningRevision-1,f.Task.PlanningRevision)]}));
        Assert.Contains("writers",error.Message);Assert.Equal(prior.ScopeRevision,(await f.Read()).ScopeRevision);
    }

    [Fact]
    public async Task UnrelatedHireKeepsTheSameEligibleSelectionButMembershipPolicyAndGrantsRemainAuthoritative()
    {
        await using var f=await HierarchicalDeliveryServiceTests.Fixture.Create(true);
        var installation=new AgentInstallation {Id=Guid.NewGuid(),PackageVersion=new() {ManifestJson="""
            {"rolePolicy":{"declaredRoleKeys":["software-developer"]},"provides":[{"name":"work.execution.run"}],"requires":[]}
            """}};
        f.Author.AgentInstallationId=installation.Id;
        var team=await f.Db.OrganizationTeams.SingleAsync(t=>t.Id==f.Board.TeamId);team.Revision=3;
        var project=await f.Db.Workstreams.SingleAsync(p=>p.Id==f.Project);project.ProfileDefinitionDigest="approved-profile";
        var membership=await f.Db.TeamMemberships.SingleOrDefaultAsync(m=>m.TeamId==team.Id&&m.OrganizationUserId==f.Author.Id);
        if(membership is null)f.Db.TeamMemberships.Add(membership=new() {Id=Guid.NewGuid(),TeamId=team.Id,OrganizationUserId=f.Author.Id});
        var grant=new AgentInstallationGrant {Id=Guid.NewGuid(),AgentInstallationId=installation.Id,ProvidedCapabilitiesJson="[\"work.execution.run\"]"};
        f.Db.AgentInstallationGrants.Add(grant);await f.Db.SaveChangesAsync();
        var assignment=new WorkItemStageAssignment {RequirementsJson=JsonSerializer.Serialize(new WorkAssignmentRequirements("game-engineer",[],[],["work.execution.run"]),Json),
            SelectionEvidenceJson=JsonSerializer.Serialize(new WorkAssignmentSelectionEvidence(installation.Id,1,"approved-profile",[],"selection",DateTimeOffset.UtcNow),Json)};
        var service=new WorkOrchestrationService(f.Db,TimeProvider.System);
        Task<string?> Check()=>service.ValidateAssignmentEvidenceAsync(f.Org,f.Board,assignment,installation,default);
        Assert.Null(await Check());team.Revision++;await f.Db.SaveChangesAsync();Assert.Null(await Check());
        membership.EndedAt=DateTimeOffset.UtcNow;await f.Db.SaveChangesAsync();Assert.Contains("active member",await Check());
        membership.EndedAt=null;grant.ProvidedCapabilitiesJson="[]";await f.Db.SaveChangesAsync();Assert.Contains("capability",await Check());
        grant.ProvidedCapabilitiesJson="[\"work.execution.run\"]";project.ProfileDefinitionDigest="changed-profile";await f.Db.SaveChangesAsync();Assert.Contains("stale",await Check());
        project.ProfileDefinitionDigest="approved-profile";installation.PackageVersion.ManifestJson="{\"rolePolicy\":{\"declaredRoleKeys\":[\"software-qa\"]},\"provides\":[],\"requires\":[]}";
        await f.Db.SaveChangesAsync();Assert.Contains("compatible core role",await Check());
    }

    [Fact]
    public async Task StaleTaskBlocksItsConsumersWhileAnIndependentDeclaredPathCompletes()
    {
        await using var f=await HierarchicalDeliveryServiceTests.Fixture.Create(true);
        var request=f.Request();var container=Guid.NewGuid();var ticket=Guid.NewGuid();
        var workflow=new WorkDeliveryTicketWorkflow(ticket,[new("collect","completion",new("collect","Human",f.Manager.Id))]) {
            Scope="Story",ScopeItemId=container,EvidenceItemIds=[]};
        f.Db.CoreWorkTasks.Add(new(){Id=container,OrganizationId=f.Org,BoardId=f.Board.Id,Kind=WorkItemKind.Story,Title="Independent preparation",IsExecutable=false,
            PlanningSpecificationJson=JsonSerializer.Serialize(new WorkItemPlanningSpecification(["Prepare independently"],["Preparation complete"]),Json)});
        f.Db.CoreWorkTasks.Add(new(){Id=ticket,OrganizationId=f.Org,BoardId=f.Board.Id,Kind=WorkItemKind.Task,TypeKey=WorkItemTypeKeys.WorkflowTicketV1,Title="Independent path",IsExecutable=false,
            PlanningSpecificationJson=JsonSerializer.Serialize(new WorkItemPlanningSpecification(["Prepare independently"],["Preparation complete"]) {Workflow=workflow.Definition()},Json)});
        await f.Db.SaveChangesAsync();
        await f.Activate(await f.Configure(request with {TicketWorkflows=[..request.TicketWorkflows,workflow]}));
        f.Task.PlanningRevision++;await f.Db.SaveChangesAsync();await f.Service.PulseAsync();
        var current=await f.Read();
        Assert.Equal("Completed",current.Executions.Single(e=>e.ItemId==container).Status);
        Assert.Equal("Blocked",current.Executions.Single(e=>e.ItemId==f.Story.Id).Status);
        Assert.DoesNotContain(current.Executions.Where(e=>e.Scope is "Epic" or "Release"),e=>e.Status=="Completed");
    }
}
