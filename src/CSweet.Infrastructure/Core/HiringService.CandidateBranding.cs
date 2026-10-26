using System.Text.Json;
using CSweet.Agent.SDK;
using HiringRecommendationResponse = CSweet.Contracts.Core.HiringRecommendationResponse;

namespace CSweet.Infrastructure.Core;

public sealed partial class HiringService
{
    private async Task<HiringRecommendationResponse> WithCurrentCandidateBrandingAsync(Guid organizationId,
        HiringRecommendationResponse card, Dictionary<(Guid OrganizationId, string Reference), AvailableAgent?> cache,
        CancellationToken token)
    {
        if (agentCatalog is null || card.ManualSelectionRequested || string.IsNullOrWhiteSpace(card.SelectedCatalogAgentJson))
            return card;
        AvailableAgent? selected;
        try { selected = JsonSerializer.Deserialize<AvailableAgent>(card.SelectedCatalogAgentJson, JsonOptions); }
        catch (JsonException) { return card; }
        // Only the embedded first-party catalog is consulted. Preserve the historical selection,
        // permissions, price and approval evidence; refresh presentation in the response only.
        if (selected?.Source != AgentCatalogSource.FirstPartyCatalog ||
            selected.AgentReference?.StartsWith("first-party:", StringComparison.Ordinal) != true)
            return card;
        var key = (organizationId, selected.AgentReference);
        if (!cache.TryGetValue(key, out var current))
            cache[key] = current = await agentCatalog.ResolveAsync(organizationId, selected.AgentReference, token);
        if (current is null || current.Source != selected.Source || current.AgentReference != selected.AgentReference ||
            string.IsNullOrWhiteSpace(selected.AgentId) || current.AgentId != selected.AgentId)
            return card;
        return card with
        {
            SelectedCatalogAgentJson = JsonSerializer.Serialize(selected with
            {
                Name = current.Name,
                ImageUrl = current.ImageUrl,
                CompanyLogoUrl = current.CompanyLogoUrl,
                AccentColor = current.AccentColor
            }, JsonOptions)
        };
    }
}
