using CSweet.Agent.SDK;
using CSweet.Infrastructure.Agents;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Marketplace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed partial class HiringServiceTests
{
    [Fact]
    public async Task AutomaticHiringNeverSelectsAManualOnlyPilotEvenWithBroadDelegation()
    {
        await using var db=CreateDb();
        var fixture=await AutonomyFixtureAsync(db);
        await fixture.Service.UpdatePolicyAsync(fixture.Organization,fixture.Installation,fixture.Application,
            new(HiringSelectionMode.Automatic,AutomaticHiringLevel.BroadDelegation),0,"Automatic hiring within approved roles");
        var options=Options.Create(new MarketplaceOptions {FirstPartyAgents=[new() {
            Id=Guid.NewGuid(),AgentId="manual.pilot",Name="Pilot",ListingSlug="manual-pilot",RoleKey="product-manager",
            RoleName="Product Manager",RepositoryUrl="https://github.com/example/pilot",ManualSelectionOnly=true}]});
        var catalog=new AgentCatalogService([new FirstPartyAgentCatalogProvider(options)],NullLogger<AgentCatalogService>.Instance,options);
        var service=new HiringService(db,new RecordingOrganizationUserService(db),new TestAuditEventWriter(),agentCatalog:catalog);
        var plan=await AutonomyPlanAsync(db,fixture.Organization,fixture.Installation,fixture.Owner);
        Assert.Null((await service.SelectCandidateAsync(fixture.Organization,fixture.Installation,plan.Id)).Candidate);
        Assert.Null(plan.SelectedCatalogAgentJson);
    }
}
