using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

// Bounded discovery of durable deletion intent also recovers after API restarts.
public sealed class OrganizationDeletionRecovery(CSweetDbContext db, CoreOrganizationService organizations)
{
    public async Task RunOnceAsync(CancellationToken token = default)
    {
        var pending = await db.CoreOrganizations.AsNoTracking()
            .Where(x => x.Status == OrganizationStatus.Deleting)
            .OrderBy(x => x.UpdatedAt).ThenBy(x => x.Id).Take(20)
            .Select(x => x.Id).ToListAsync(token);
        foreach (var id in pending)
        {
            await organizations.ContinueDeletionAsync(id, token);
        }
    }
}
