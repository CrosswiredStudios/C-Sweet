namespace CSweet.Contracts.Marketplace;

public sealed record MarketplaceAgentResponse(
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
    string ListingUrl,
    bool IsFirstParty = false,
    string? RoleKey = null,
    string? RoleName = null,
    IReadOnlyList<string>? RoleAliases = null,
    IReadOnlyList<string>? Keywords = null,
    string? LicenseSpdxId = null,
    string? LicenseUrl = null,
    IReadOnlyList<string>? IconUrls = null);

public sealed record MarketplaceDiscoveryResponse(
    IReadOnlyList<MarketplaceAgentResponse> Items,
    int Total,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> PricingModels,
    bool IsOnline,
    string? UnavailableReason,
    IReadOnlyList<MarketplaceAgentResponse>? FirstPartyItems = null);

public sealed record MarketplaceDiscoveryQuery(
    string? Search = null,
    string? Category = null,
    string? Capability = null,
    string? PricingModel = null,
    decimal? MaximumPrice = null,
    string? Sort = null,
    int Take = 24);
