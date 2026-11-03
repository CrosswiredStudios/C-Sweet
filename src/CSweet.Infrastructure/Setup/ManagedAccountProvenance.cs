using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

internal static class ManagedAccountProvenance
{
    internal static async Task<PluginAccountSetupSession> Original(CSweetDbContext db,Guid installation,Guid connection,CancellationToken ct)
    {
        var candidates=await db.PluginAccountSetupSessions.AsNoTracking().Where(x=>x.ConnectionId==connection && !x.IsMaintenance).Take(2).ToArrayAsync(ct);
        if(candidates.Length!=1)throw new UnauthorizedAccessException("An unambiguous original managed consent is required.");
        var original=candidates[0];
        if(original.PluginInstallationId!=installation && !await db.PluginAccountUpgrades.AsNoTracking().AnyAsync(x=>x.TargetInstallationId==installation &&
            x.ConnectionId==connection && x.OriginalSetupSessionId==original.Id && x.AccountGroupId==original.AccountGroupId && x.Status=="Completed",ct))
            throw new UnauthorizedAccessException("The original consent does not belong to this revision.");
        return original;
    }
}
