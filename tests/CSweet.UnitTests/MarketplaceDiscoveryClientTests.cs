using System.Net;
using System.Net.Http.Json;
using CSweet.Agent.SDK;
using CSweet.Contracts.Marketplace;
using CSweet.Infrastructure.Marketplace;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class MarketplaceDiscoveryClientTests
{
    [Fact]
    public async Task Online_catalog_supports_browsing_and_chief_of_staff_workforce_search()
    {
        var handler = new StubHandler(new
        {
            items = new[]
            {
                new
                {
                    id = Guid.Parse("d8b9808d-c28f-47ac-86f0-13f77bf25ead"),
                    publisherSlug = "trusted-studio",
                    listingSlug = "research-agent",
                    name = "Research Agent",
                    publisherName = "Trusted Studio",
                    summary = "Produces sourced market research.",
                    category = "Research",
                    capabilities = new[] { "research.market", "artifact.report" },
                    pricingModel = "MonthlyPerInstance",
                    priceInCents = 2500,
                    billingUnitQuantity = 1,
                    currency = "USD",
                    rating = 9.2m,
                    ratingCount = 18,
                    isFeatured = true,
                    repositoryUrl = "https://github.com/example/research-agent",
                    documentationUrl = "https://github.com/example/research-agent#readme",
                    listingPath = "/marketplace/trusted-studio/research-agent"
                }
            },
            total = 1,
            categories = new[] { "Research" },
            pricingModels = new[] { "MonthlyPerInstance" }
        });
        var client = Client(handler, enabled: true);

        var browse = await client.SearchAsync(
            new MarketplaceDiscoveryQuery(Capability: "research.market"));
        var listing = Assert.Single(browse.Items);
        Assert.True(browse.IsOnline);
        Assert.Equal("https://marketplace.test/marketplace/trusted-studio/research-agent",
            listing.ListingUrl);

        var workforce = await client.SearchAsync(new WorkforceSearchRequest(
            ["research.market"], null, null, 30m, "USD", false, null));
        var candidate = Assert.Single(workforce.Candidates);
        Assert.True(workforce.MarketplaceAvailable);
        Assert.Equal("CSweetMarketplace", candidate.Source);
        Assert.Equal(25m, candidate.EstimatedCost);
        Assert.Contains("marketplace.test", candidate.Rationale);
        Assert.DoesNotContain("Produces sourced market research", candidate.Rationale);
        Assert.DoesNotContain("q=", handler.LastRequestUri!.Query);
    }

    [Fact]
    public async Task Disabled_catalog_reports_offline_without_making_a_request()
    {
        var handler = new StubHandler(new { });
        var client = Client(handler, enabled: false);

        var browse = await client.SearchAsync(new MarketplaceDiscoveryQuery());
        var workforce = await client.SearchAsync(new WorkforceSearchRequest(
            ["research.market"], null, null, null, "USD", false, null));

        Assert.False(browse.IsOnline);
        Assert.False(workforce.MarketplaceAvailable);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task First_party_agents_remain_available_when_marketplace_is_offline()
    {
        var handler = new StubHandler(new { });
        var firstParty = new FirstPartyMarketplaceAgentOptions
        {
            Id = Guid.Parse("01fb264d-1cd9-4bc6-a5d2-08adc63a5f32"),
            ListingSlug = "chief-of-staff",
            Name = "Chief of Staff",
            Summary = "Coordinates executive priorities.",
            Category = "Leadership",
            Capabilities = ["executive.operations", "workforce.plan"],
            IsFeatured = true,
            RepositoryUrl = "https://github.com/CrosswiredStudios/CSweet.Agent.ChiefOfStaff"
        };
        var client = Client(handler, enabled: false, [firstParty]);

        var browse = await client.SearchAsync(new MarketplaceDiscoveryQuery());

        Assert.False(browse.IsOnline);
        var agent = Assert.Single(browse.Items);
        Assert.True(agent.IsFirstParty);
        Assert.True(agent.IsFeatured);
        Assert.Equal("C-Sweet", agent.PublisherName);
        Assert.Equal(agent, Assert.Single(browse.FirstPartyItems!));
        Assert.Equal(0, handler.RequestCount);
    }

    private static MarketplaceDiscoveryClient Client(
        StubHandler handler,
        bool enabled,
        List<FirstPartyMarketplaceAgentOptions>? firstPartyAgents = null)
    {
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://marketplace.test/")
        };
        return new MarketplaceDiscoveryClient(http, Options.Create(new MarketplaceOptions
        {
            Enabled = enabled,
            BaseUrl = "https://marketplace.test/",
            TimeoutSeconds = 5,
            FirstPartyAgents = firstPartyAgents ?? []
        }));
    }

    private sealed class StubHandler(object body) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(body)
            });
        }
    }
}
