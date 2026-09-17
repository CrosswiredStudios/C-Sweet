using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Analytics;

public static class BenchmarkHumanPolicy
{
    public static async Task<bool> AllowsAsync(CSweetDbContext db, Guid organizationId, bool approval, CancellationToken ct)
    {
        var trial = await db.BenchmarkTrials.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            (x.Status == "Running" || x.Status == "Blocked"), ct);
        if (trial is null) return true;
        var definition = await (from c in db.BenchmarkCampaigns join d in db.BenchmarkDefinitions on c.DefinitionId equals d.Id
            where c.Id == trial.CampaignId select d).AsNoTracking().SingleAsync(ct);
        var b = BenchmarkService.Blueprint(definition);
        return b.AssistanceMode == "Assisted" && (!b.ApprovalsOnly || approval);
    }
}
