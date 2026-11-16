using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class ResourceChangeServiceTests
{
    [Theory]
    [InlineData("owner")]
    [InlineData("manager")]
    [InlineData("team")]
    [InlineData("project")]
    [InlineData("stale")]
    public async Task Revision_lineage_rejects_foreign_scope_and_unanswered_old_reviews(string mismatch)
    {
        await using var db = CreateDb(); var setup = SeedManagerConversation(db); await db.SaveChangesAsync();
        var service = new ResourceChangeService(db, new TestAuditEventWriter());
        var original = await service.ProposeAsync(setup.OrganizationId, setup.RequesterInstallationId, Proposal(setup, "original-review"));
        await service.DecideForInstallationAsync(setup.OrganizationId, setup.ManagerInstallationId, new(original.Id, "RequestRevision", "Change coverage", "return-original"));
        var stored = await db.ResourceChangeRequests.SingleAsync(r => r.Id == original.Id);
        switch (mismatch)
        {
            case "owner": stored.RequesterOrganizationUserId = Guid.NewGuid(); break;
            case "manager": stored.ManagerOrganizationUserId = Guid.NewGuid(); break;
            case "team": stored.TeamId = Guid.NewGuid(); break;
            case "project": stored.WorkstreamId = Guid.NewGuid(); break;
            case "stale": await service.ProposeAsync(setup.OrganizationId, setup.RequesterInstallationId, Proposal(setup, "newer-proposal")); break;
        }
        await db.SaveChangesAsync();
        var draft = Proposal(setup, "foreign-lineage") with { RevisesRequestId = original.Id };
        if (mismatch == "stale") await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.ProposeAsync(setup.OrganizationId, setup.RequesterInstallationId, draft));
        else await Assert.ThrowsAsync<ArgumentException>(() => service.ProposeAsync(setup.OrganizationId, setup.RequesterInstallationId, draft));
    }

    [Fact]
    public async Task Verbose_staffing_and_reviews_are_preserved_through_broker_and_storage()
    {
        await using var db=CreateDb(); var setup=SeedManagerConversation(db); await db.SaveChangesAsync();
        var manager=await db.CoreOrganizationUsers.SingleAsync(x=>x.AgentInstallationId==setup.ManagerInstallationId);
        var text=new string('x',12000)+" Final independent escalation responsibility.";
        var proposal=Proposal(setup,"verbose-staffing") with { ProductGoal=text,Rationale=text, ExpectedEffect=text,
            TeamKey="research-team", TeamName="Research team", TeamDescription=text,
            Assumptions=[text], Constraints=[text], AlternativesConsidered=[text],
            Roles=Proposal(setup,"unused").Roles.Select(r=>r with { Purpose=text, Timing=text, ReportsToOrganizationUserId=manager.Id }).ToArray() };
        var tool=Assert.Single(new CSweet.AgentHost.Broker.McpToolCatalog([]).List(new HashSet<string>{ResourceChangeCapabilities.Propose}));
        CSweet.AgentHost.Broker.JsonSchemaValidator.Validate(System.Text.Json.JsonSerializer.SerializeToElement(proposal,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),tool.InputSchema);
        var service=new ResourceChangeService(db,new TestAuditEventWriter(),new TeamService(db,new TestAuditEventWriter(),TimeProvider.System));
        var saved=await service.ProposeAsync(setup.OrganizationId,setup.RequesterInstallationId,proposal);
        Assert.Equal(text,saved.ProductGoal); Assert.Equal(text,saved.Rationale); Assert.Equal(text,saved.ExpectedEffect);
        Assert.All(saved.Roles,r=>{Assert.Equal(text,r.Purpose); Assert.Equal(text,r.Timing); Assert.Equal(manager.Id,r.ReportsToOrganizationUserId);});
        var returned=await service.DecideForInstallationAsync(setup.OrganizationId,setup.ManagerInstallationId,new(saved.Id,"RequestRevision",text,"verbose-review"));
        Assert.Equal(text,returned.DecisionComment);
        db.ChangeTracker.Clear();
        Assert.Equal(text,(await db.ResourceChangeRequests.SingleAsync()).DecisionComment);
        Assert.All(await db.ResourceChangeRoles.ToListAsync(),r=>Assert.Equal(text,r.Purpose));
        var revision=await service.ProposeAsync(setup.OrganizationId,setup.RequesterInstallationId,
            proposal with {IdempotencyKey="approve-verbose-team",RevisesRequestId=saved.Id});
        await service.DecideForInstallationAsync(setup.OrganizationId,setup.ManagerInstallationId,new(revision.Id,"Approve",text,"verbose-team-approval"));
        Assert.Equal(text,(await db.OrganizationTeams.SingleAsync()).Description);
    }

    [Fact]
    public async Task Each_returned_review_has_one_replacement_and_keeps_approved_baseline_separate()
    {
        await using var db=CreateDb(); var setup=SeedManagerConversation(db); await db.SaveChangesAsync();
        var manager=await db.CoreOrganizationUsers.SingleAsync(x=>x.AgentInstallationId==setup.ManagerInstallationId);
        var service=new ResourceChangeService(db,new TestAuditEventWriter());
        var next=await service.ProposeAsync(setup.OrganizationId,setup.RequesterInstallationId,Proposal(setup,"round-0"));
        for(var i=1;i<=4;i++)
        {
            var returned=await service.DecideForInstallationAsync(setup.OrganizationId,setup.ManagerInstallationId,new(next.Id,"RequestRevision","Review "+i,"decision-"+i));
            var draft=Proposal(setup,"round-"+i) with { RevisesRequestId=returned.Id,Rationale="Changed judgment "+i };
            next=await service.ProposeAsync(setup.OrganizationId,setup.RequesterInstallationId,draft);
            var replay=await service.ProposeAsync(setup.OrganizationId,setup.RequesterInstallationId,draft with { IdempotencyKey="other-delivery-"+i });
            Assert.Equal(next.Id,replay.Id); Assert.Equal(returned.Id,next.RevisesRequestId); Assert.Null(next.SupersedesRequestId);
        }
        Assert.Equal(5,await db.ResourceChangeRequests.CountAsync());
        var approved=await service.DecideForInstallationAsync(setup.OrganizationId,setup.ManagerInstallationId,new(next.Id,"Approve","Work justified","approve-final"));
        Assert.Equal("Approved",approved.Status);
        await Assert.ThrowsAsync<ArgumentException>(()=>service.ProposeAsync(setup.OrganizationId,setup.RequesterInstallationId,
            Proposal(setup,"cannot-revise-approved") with { RevisesRequestId=approved.Id }));
    }
}
