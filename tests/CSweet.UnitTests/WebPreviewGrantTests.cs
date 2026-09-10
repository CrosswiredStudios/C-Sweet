using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.WebHost.Contracts;
using Microsoft.EntityFrameworkCore;
namespace CSweet.UnitTests;

public sealed class WebPreviewGrantTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-09-10T12:00:00Z");
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow()=>Now; }
    private sealed class Fixture : IAsyncDisposable
    {
        public CSweetDbContext Db { get; } = new(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        public Guid OrganizationId { get; }=Guid.NewGuid();
        public AgentInstallation Agent { get; }=new() { Id=Guid.NewGuid() };
        public AgentInstallation Provider { get; }=new() { Id=Guid.NewGuid() };
        public OrganizationUser Actor { get; }=new() { Id=Guid.NewGuid(),DisplayName="Developer",EmployeeType=EmployeeType.Agent };
        public OrganizationUser Owner { get; }=new() { Id=Guid.NewGuid(),DisplayName="Owner",EmployeeType=EmployeeType.Human,
            ApplicationUserId=Guid.NewGuid(),PermissionLevel=OrganizationPermissionLevel.Owner };
        public Workstream Workstream { get; }=new() { Id=Guid.NewGuid(),Name="Demo" };
        public SourceControlRepository Repository { get; }=new() { Id=Guid.NewGuid(),Name="Product" };
        public WebPreviewGrantService Service => new(Db,new Clock());
        public RequestPreviewGrant Request => new(Workstream.Id,Provider.Id,[Repository.Id],ResourceBudget.Default,2,100000,
            7200,Now.AddDays(1),"Test a private product demo.","test-grant");
        public async Task SeedAsync()
        {
            Agent.BusinessId=Provider.BusinessId=OrganizationId.ToString("D");
            Agent.Grant=new() { Id=Guid.NewGuid(),AgentInstallationId=Agent.Id,RequiredCapabilitiesJson=JsonSerializer.Serialize(WebPreviewCapabilities.All) };
            Agent.PackageVersion=new() { Id=Guid.NewGuid(),AgentId="test-agent" }; Agent.PackageVersionId=Agent.PackageVersion.Id;
            Provider.PackageVersion=new() { Id=Guid.NewGuid(),AgentId=WebPreviewGrantService.PluginId }; Provider.PackageVersionId=Provider.PackageVersion.Id;
            Actor.OrganizationId=Owner.OrganizationId=Workstream.OrganizationId=Repository.OrganizationId=OrganizationId;
            Actor.AgentInstallationId=Agent.Id; Workstream.AccountableManagerOrganizationUserId=Actor.Id;
            Db.AgentInstallations.AddRange(Agent,Provider); Db.CoreOrganizationUsers.AddRange(Actor,Owner);
            Db.Workstreams.Add(Workstream); Db.SourceControlRepositories.Add(Repository); await Db.SaveChangesAsync();
        }
        public async ValueTask DisposeAsync()=>await Db.DisposeAsync();
    }
    [Fact] public async Task Requests_reuse_exact_terms_and_remain_inactive_until_owner_approves()
    {
        await using var f=new Fixture(); await f.SeedAsync();
        var first=await f.Service.RequestAsync(f.OrganizationId,f.Agent.Id,f.Request,default);
        var retry=await f.Service.RequestAsync(f.OrganizationId,f.Agent.Id,f.Request,default);
        Assert.Equal(first,retry); Assert.Equal("Pending",first.Status);
        Assert.Single(await f.Db.WebPreviewGrants.ToListAsync()); Assert.Single(await f.Db.ActionProposals.ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.RequestAsync(f.OrganizationId,f.Agent.Id,
            f.Request with { MaximumCpuSeconds=200000 },default));
        var proposal=await f.Db.ActionProposals.SingleAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Service.ExecuteAsync(proposal,f.Actor));
        var result=await f.Service.ExecuteAsync(proposal,f.Owner);
        proposal.Status=ProposalStatus.Approved; await f.Db.SaveChangesAsync();
        var grant=await f.Db.WebPreviewGrants.SingleAsync();
        Assert.Equal("Active",grant.Status); Assert.Equal(2,result.Revision); Assert.Equal(f.Owner.Id,grant.ApprovedByOrganizationUserId);
        Assert.Equal(first.ApprovalRevisionId,(await f.Db.CoreApprovals.SingleAsync()).ArtifactRevisionId);
    }
    [Fact] public async Task Rejects_changed_review_document_and_disabled_provider_at_approval_time()
    {
        await using var f=new Fixture(); await f.SeedAsync();
        await f.Service.RequestAsync(f.OrganizationId,f.Agent.Id,f.Request,default);
        var proposal=await f.Db.ActionProposals.SingleAsync();
        var artifact=await f.Db.CoreArtifacts.SingleAsync(); var original=artifact.Content;
        artifact.Content="Expanded limits"; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.ExecuteAsync(proposal,f.Owner));
        artifact.Content=original; f.Provider.IsEnabled=false; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Service.ExecuteAsync(proposal,f.Owner));
        Assert.Equal("Pending",(await f.Db.WebPreviewGrants.SingleAsync()).Status);
    }
    [Fact] public async Task Rejects_cross_organization_repository_and_workstream_and_stale_capabilities()
    {
        await using var f=new Fixture(); await f.SeedAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Service.RequestAsync(f.OrganizationId,f.Agent.Id,
            f.Request with { ProjectId=Guid.NewGuid() },default));
        var foreign=new SourceControlRepository { Id=Guid.NewGuid(),OrganizationId=Guid.NewGuid(),Name="Foreign" };
        f.Db.SourceControlRepositories.Add(foreign); await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Service.RequestAsync(f.OrganizationId,f.Agent.Id,f.Request with { RepositoryIds=[foreign.Id] },default));
        f.Agent.Grant!.RequiredCapabilitiesJson="[]"; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Service.RequestAsync(f.OrganizationId,f.Agent.Id,f.Request,default));
        Assert.Empty(await f.Db.WebPreviewGrants.ToListAsync());
    }
    [Fact] public async Task Preflight_does_not_claim_a_grant_is_a_running_host()
    {
        await using var f=new Fixture(); await f.SeedAsync();
        var request=new PreviewRequest(f.Workstream.Id,f.Repository.Id,f.Provider.Id,
            new(1,PreviewMode.Static,new string('a',40),"sha256:"+new string('b',64),JsonSerializer.SerializeToElement<object?>(null),
                null,ResourceBudget.Default,7200,[]),"preview");
        Assert.Contains((await f.Service.PreflightAsync(f.OrganizationId,f.Agent.Id,request,default)).Problems,x=>x.Code=="GrantRequired");
        await f.Service.RequestAsync(f.OrganizationId,f.Agent.Id,f.Request,default);
        var proposal=await f.Db.ActionProposals.SingleAsync(); await f.Service.ExecuteAsync(proposal,f.Owner);
        proposal.Status=ProposalStatus.Approved; await f.Db.SaveChangesAsync();
        var preflight=await f.Service.PreflightAsync(f.OrganizationId,f.Agent.Id,request,default);
        Assert.False(preflight.Allowed); Assert.Contains(preflight.Problems,x=>x.Code=="RuntimeUnavailable");
        var grant=await f.Db.WebPreviewGrants.SingleAsync(); grant.RevokedAt=Now; await f.Db.SaveChangesAsync();
        Assert.Contains((await f.Service.PreflightAsync(f.OrganizationId,f.Agent.Id,request,default)).Problems,x=>x.Code=="GrantRequired");
    }
    [Fact] public async Task Owner_can_revoke_a_standing_grant_and_review_shows_exact_limits()
    {
        await using var f=new Fixture(); await f.SeedAsync();
        var request=await f.Service.RequestAsync(f.OrganizationId,f.Agent.Id,f.Request,default);
        var proposal=await f.Db.ActionProposals.SingleAsync();
        var review=CSweet.Infrastructure.Core.ApprovalDashboardService.ReadManagedAction(proposal);
        Assert.NotNull(review?.ReviewPayloadJson);
        Assert.True(CSweet.UI.Components.WebPreviewGrantReview.IsValid(review!.ReviewPayloadJson));
        await f.Service.ExecuteAsync(proposal,f.Owner); proposal.Status=ProposalStatus.Approved; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Service.RevokeAsync(f.OrganizationId,request.Id,Guid.NewGuid(),default));
        var revoked=await f.Service.RevokeAsync(f.OrganizationId,request.Id,f.Owner.ApplicationUserId!.Value,default);
        Assert.Equal("Revoked",revoked.Status);
        Assert.NotNull((await f.Db.WebPreviewGrants.SingleAsync()).RevokedAt);
        Assert.Equal(revoked,await f.Service.RevokeAsync(f.OrganizationId,request.Id,f.Owner.ApplicationUserId.Value,default));
    }
    [Fact] public void Request_and_preflight_tools_have_reviewable_schemas()
    {
        var catalog=new McpToolCatalog([]);
        var tools=catalog.List(new HashSet<string> { WebPreviewCapabilities.RequestGrant,WebPreviewCapabilities.Preflight });
        Assert.Equal(2,tools.Count);
        Assert.Equal(McpToolExecutionPolicy.ApprovalCreating,tools.Single(x=>x.Capability==WebPreviewCapabilities.RequestGrant).ExecutionPolicy);
        Assert.Contains(tools.Single(x=>x.Capability==WebPreviewCapabilities.RequestGrant).InputSchema.GetProperty("required").EnumerateArray(),
            x=>x.GetString()=="maximumCpuSeconds");
    }
}
