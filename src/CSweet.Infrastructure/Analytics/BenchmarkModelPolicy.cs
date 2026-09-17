using System.Text.Json;
using CSweet.Contracts.Analytics;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Analytics;

public static class BenchmarkModelPolicy
{
    public static async Task ApplyAsync(CSweetDbContext db, Guid installationId, string businessId,
        IDictionary<string, JsonElement> settings, CancellationToken ct)
    {
        var model = await ResolveAsync(db, installationId, businessId, ct);
        if (model is null) return;
        settings["llmProviderId"] = JsonSerializer.SerializeToElement(model.ProviderProfileId.ToString());
        settings["llmModel"] = JsonSerializer.SerializeToElement(model.Model);
    }

    public static async Task<BenchmarkModel?> ResolveAsync(CSweetDbContext db, Guid installationId,
        string businessId, CancellationToken ct)
    {
        if (!Guid.TryParse(businessId, out var organization)) return null;
        var trial = await db.BenchmarkTrials.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organization, ct);
        if (trial is null) return null;
        var definition = await (from campaign in db.BenchmarkCampaigns
            join d in db.BenchmarkDefinitions on campaign.DefinitionId equals d.Id
            where campaign.Id == trial.CampaignId select d).AsNoTracking().SingleAsync(ct);
        var b = BenchmarkService.Blueprint(definition);
        var variant = b.Variants[trial.VariantIndex];
        var role = await db.CoreOrganizationUsers.Where(x => x.OrganizationId == organization && x.AgentInstallationId == installationId)
            .Select(x => x.Role!.Name).SingleOrDefaultAsync(ct);
        return role is not null && variant.RoleOverrides.TryGetValue(role, out var selected) ? selected : variant.DefaultModel;
    }
}
