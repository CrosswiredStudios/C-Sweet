using CSweet.Agent.SDK;
using CSweet.Infrastructure.Agents;
using CSweet.Infrastructure.Marketplace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class ManualOnlyAgentCatalogTests
{
    [Fact]
    public async Task PilotIsDiscoverableForOwnerSelectionButNeverAutomaticEvenAfterInstallation()
    {
        var id=Guid.NewGuid();
        var options=Options.Create(new MarketplaceOptions {FirstPartyAgents=[new() {
            Id=id,AgentId="pilot.developer",Name="Pilot",RoleKey="software-developer",RoleName="Software Developer",
            ListingSlug="pilot",RepositoryUrl="https://github.com/example/pilot",ManualSelectionOnly=true}]});
        var catalog=new AgentCatalogService([new FirstPartyAgentCatalogProvider(options)],NullLogger<AgentCatalogService>.Instance,options);
        var pilot=Assert.Single((await catalog.GetAvailableAgentsAsync(Guid.NewGuid(),new(SearchString:"Pilot"))).Agents);
        Assert.Equal(AgentAvailabilityState.AvailableToInstall,pilot.Availability);
        Assert.NotNull(await catalog.ResolveAsync(Guid.NewGuid(),pilot.AgentReference));
        Assert.False(catalog.IsAutomaticSelectionAllowed(pilot));
        Assert.False(catalog.IsAutomaticSelectionAllowed(pilot with {Source=AgentCatalogSource.Installed,AgentReference="installed:"+Guid.NewGuid().ToString("N")}));
        Assert.True(catalog.IsAutomaticSelectionAllowed(pilot with {AgentId="existing.developer",AgentReference="first-party:"+Guid.NewGuid().ToString("N")}));
    }
}
