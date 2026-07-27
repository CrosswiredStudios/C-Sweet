using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net.Http.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Marketplace;
using CSweet.Contracts.Marketplace;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Marketplace;

public sealed class MarketplaceOptions
{
    public const string SectionName = "CSweet:Marketplace";

    public bool Enabled { get; set; }

    [Required, Url]
    public string BaseUrl { get; set; } = "https://marketplace.csweet.com/";

    [Range(1, 60)]
    public int TimeoutSeconds { get; set; } = 10;

    public List<FirstPartyMarketplaceAgentOptions> FirstPartyAgents { get; set; } = [];
}

public sealed class FirstPartyMarketplaceAgentOptions
{
    public Guid Id { get; set; }
    public string AgentId { get; set; } = string.Empty;
    public string ListingSlug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Category { get; set; } = "Operations";
    public List<string> Capabilities { get; set; } = [];
    public List<string> RoleAliases { get; set; } = [];
    public List<string> Keywords { get; set; } = [];
    public bool IsFeatured { get; set; }
    public string Availability { get; set; } = "Available";
    public string RepositoryUrl { get; set; } = string.Empty;
    public string DocumentationUrl { get; set; } = string.Empty;
}

public sealed class MarketplaceDiscoveryClient(
    HttpClient http,
    IOptions<MarketplaceOptions> options)
    : IMarketplaceDiscoveryService, IWorkforceCatalogProvider
{
    public string ProviderKey => "csweet-marketplace";
    public WorkforceCatalogKind CatalogKind => WorkforceCatalogKind.DigitalMarketplace;

    public async Task<MarketplaceDiscoveryResponse> SearchAsync(
        MarketplaceDiscoveryQuery query,
        CancellationToken cancellationToken = default)
    {
        var firstPartyAgents = FirstPartyAgents();
        if (!options.Value.Enabled)
            return Offline(
                "The online C-Sweet Marketplace is disabled for this installation.",
                query,
                firstPartyAgents);

        try
        {
            using var response = await http.GetAsync(BuildPath(query), cancellationToken);
            if (!response.IsSuccessStatusCode)
                return Offline(
                    $"Marketplace discovery returned HTTP {(int)response.StatusCode}.",
                    query,
                    firstPartyAgents);
            var remote = await response.Content.ReadFromJsonAsync<RemoteDiscoveryResponse>(
                cancellationToken: cancellationToken);
            if (remote is null)
                return Offline(
                    "Marketplace discovery returned an empty response.",
                    query,
                    firstPartyAgents);

            var remoteItems = remote.Items.Select(Map).ToArray();
            var matchingFirstParty = FilterFirstParty(firstPartyAgents, query);
            var repositories = matchingFirstParty
                .Select(x => x.RepositoryUrl)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var items = matchingFirstParty
                .Concat(remoteItems.Where(x => !repositories.Contains(x.RepositoryUrl)))
                .ToArray();

            return new MarketplaceDiscoveryResponse(
                items,
                remote.Total + matchingFirstParty.Count,
                remote.Categories
                    .Concat(firstPartyAgents.Select(x => x.Category))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x)
                    .ToArray(),
                remote.PricingModels
                    .Append("OpenSourceFree")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x)
                    .ToArray(),
                true,
                null,
                firstPartyAgents);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or NotSupportedException)
        {
            return Offline(
                "The online C-Sweet Marketplace is currently unavailable.",
                query,
                firstPartyAgents);
        }
    }

    public async Task<WorkforceSearchResponse> SearchAsync(
        WorkforceSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var discovery = await SearchAsync(new MarketplaceDiscoveryQuery(
            Search: null,
            Capability: request.RequiredCapabilities.FirstOrDefault(),
            MaximumPrice: request.MaximumBudget,
            Sort: "rating",
            Take: Math.Clamp(request.MaximumResults * 3, 1, 100)), cancellationToken);
        var embeddedCatalogAvailable = discovery.FirstPartyItems is { Count: > 0 };
        if (!discovery.IsOnline && !embeddedCatalogAvailable)
            return new WorkforceSearchResponse([], [], false, discovery.UnavailableReason);

        var accepted = new List<WorkforceCandidate>();
        var rejected = new List<RejectedWorkforceCandidate>();
        foreach (var agent in discovery.Items)
        {
            var source = agent.IsFirstParty ? "CSweetEmbeddedCatalog" : "CSweetMarketplace";
            var missing = request.RequiredCapabilities
                .Except(agent.Capabilities, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var reasons = new List<string>();
            reasons.AddRange(missing.Select(x => $"Missing capability {x}."));
            if (request.RequiredCredentials is { Count: > 0 })
                reasons.Add("The marketplace listing does not provide verified credential evidence.");
            if (!string.IsNullOrWhiteSpace(request.Currency) &&
                !string.Equals(request.Currency, agent.Currency, StringComparison.OrdinalIgnoreCase))
                reasons.Add($"Price is denominated in {agent.Currency}, not {request.Currency}.");
            var price = agent.PriceInCents / 100m;
            if (request.MaximumBudget is { } maximum && price is { } amount && amount > maximum)
                reasons.Add("Listed price exceeds the requested budget.");
            if (reasons.Count > 0)
            {
                rejected.Add(new RejectedWorkforceCandidate(
                    agent.Id.ToString("D"), agent.Name, source, reasons));
                continue;
            }

            var ratingScore = agent.Rating is { } rating
                ? Math.Clamp(rating / 10m, 0m, 1m)
                : 0.5m;
            var score = Math.Min(0.99m,
                0.55m + ratingScore * 0.35m + (agent.IsFeatured ? 0.05m : 0m));
            accepted.Add(new WorkforceCandidate(
                agent.Id.ToString("D"),
                source,
                "Agent",
                agent.Name,
                agent.Capabilities,
                [],
                price,
                agent.Currency,
                score,
                agent.IsFirstParty
                    ? $"Embedded first-party agent matched the requested capabilities. Its installable source is {agent.RepositoryUrl}"
                    : agent.Rating is { } scoreRating
                    ? $"Marketplace listing matched the requested capabilities. Current six-month rating: {scoreRating:0.0}/10 from {agent.RatingCount} review(s). Review and acquire it at {agent.ListingUrl}"
                    : $"Marketplace listing matched the requested capabilities. Review and acquire it at {agent.ListingUrl}",
                true)
            {
                RepositoryUrl = agent.RepositoryUrl
            });
        }

        return new WorkforceSearchResponse(
            accepted.OrderByDescending(x => x.Score)
                .Take(Math.Clamp(request.MaximumResults, 1, 25)).ToArray(),
            rejected,
            discovery.IsOnline || embeddedCatalogAvailable,
            discovery.IsOnline ? null : discovery.UnavailableReason);
    }

    private string BuildPath(MarketplaceDiscoveryQuery query)
    {
        var parameters = new List<string>();
        Add("q", query.Search);
        Add("category", query.Category);
        Add("capability", query.Capability);
        Add("pricing", query.PricingModel);
        Add("maxPrice", query.MaximumPrice?.ToString(CultureInfo.InvariantCulture));
        Add("sort", query.Sort);
        Add("take", Math.Clamp(query.Take, 1, 100).ToString(CultureInfo.InvariantCulture));
        return "api/v1/discovery/agents" +
            (parameters.Count == 0 ? string.Empty : $"?{string.Join('&', parameters)}");

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                parameters.Add($"{key}={Uri.EscapeDataString(value)}");
        }
    }

    private MarketplaceAgentResponse Map(RemoteAgent item) =>
        new(item.Id, item.PublisherSlug, item.ListingSlug, item.Name, item.PublisherName,
            item.Summary, item.Category, item.Capabilities, item.PricingModel,
            item.PriceInCents, item.BillingUnitQuantity, item.Currency, item.Rating,
            item.RatingCount, item.IsFeatured, item.RepositoryUrl, item.DocumentationUrl,
            new Uri(http.BaseAddress!, item.ListingPath).ToString());

    private IReadOnlyList<MarketplaceAgentResponse> FirstPartyAgents() =>
        options.Value.FirstPartyAgents
            .Where(x =>
                x.Id != Guid.Empty &&
                !string.IsNullOrWhiteSpace(x.ListingSlug) &&
                !string.IsNullOrWhiteSpace(x.Name) &&
                Uri.TryCreate(x.RepositoryUrl, UriKind.Absolute, out _))
            .Select(x => new MarketplaceAgentResponse(
                x.Id,
                "crosswired-studios",
                x.ListingSlug,
                x.Name,
                "C-Sweet",
                x.Summary,
                x.Category,
                x.Capabilities,
                "OpenSourceFree",
                null,
                1,
                "USD",
                null,
                0,
                x.IsFeatured,
                x.RepositoryUrl,
                string.IsNullOrWhiteSpace(x.DocumentationUrl)
                    ? $"{x.RepositoryUrl}#readme"
                    : x.DocumentationUrl,
                x.RepositoryUrl,
                true))
            .ToArray();

    private static IReadOnlyList<MarketplaceAgentResponse> FilterFirstParty(
        IReadOnlyList<MarketplaceAgentResponse> agents,
        MarketplaceDiscoveryQuery query)
    {
        IEnumerable<MarketplaceAgentResponse> filtered = agents;
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            filtered = filtered.Where(x =>
                x.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                x.Summary.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                x.Category.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                x.Capabilities.Any(capability =>
                    capability.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }
        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            filtered = filtered.Where(x =>
                string.Equals(x.Category, query.Category.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(query.Capability))
        {
            filtered = filtered.Where(x =>
                x.Capabilities.Any(capability =>
                    capability.Contains(query.Capability.Trim(), StringComparison.OrdinalIgnoreCase)));
        }
        if (!string.IsNullOrWhiteSpace(query.PricingModel) &&
            query.PricingModel is not ("OpenSourceFree" or "Free"))
        {
            filtered = [];
        }

        return filtered.Take(Math.Clamp(query.Take, 1, 100)).ToArray();
    }

    private static MarketplaceDiscoveryResponse Offline(
        string reason,
        MarketplaceDiscoveryQuery query,
        IReadOnlyList<MarketplaceAgentResponse> firstPartyAgents)
    {
        var matchingFirstParty = FilterFirstParty(firstPartyAgents, query);
        return new MarketplaceDiscoveryResponse(
            matchingFirstParty,
            matchingFirstParty.Count,
            firstPartyAgents.Select(x => x.Category)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToArray(),
            ["OpenSourceFree"],
            false,
            reason,
            firstPartyAgents);
    }

    private sealed record RemoteDiscoveryResponse(
        IReadOnlyList<RemoteAgent> Items,
        int Total,
        IReadOnlyList<string> Categories,
        IReadOnlyList<string> PricingModels);

    private sealed record RemoteAgent(
        Guid Id,
        string PublisherSlug,
        string ListingSlug,
        string Name,
        string PublisherName,
        string Summary,
        string Category,
        IReadOnlyList<string> Capabilities,
        string PricingModel,
        int? PriceInCents,
        int BillingUnitQuantity,
        string Currency,
        decimal? Rating,
        int RatingCount,
        bool IsFeatured,
        string RepositoryUrl,
        string DocumentationUrl,
        string ListingPath);
}
