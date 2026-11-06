using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Agents;

public static class AgentGovernanceAccess
{
    public static async Task<bool> CanManageAsync(CSweetDbContext db, Guid user, Guid installation,
        bool administrator = false, CancellationToken token = default)
    {
        var target = await db.AgentInstallations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == installation, token);
        if (target is null) return false;
        if (administrator) return true;
        if (target.Scope == PluginInstallationScope.User) return target.OwnerApplicationUserId == user;
        if (!Guid.TryParse(target.BusinessId, out var org)) return false;
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == org &&
            x.ApplicationUserId == user && x.IsActive && x.ArchivedAt == null, token);
        if (actor is null || actor.EmployeeType != EmployeeType.Human) return false;
        if (!await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.OrganizationId == org && x.AgentInstallationId == installation && x.IsActive && x.ArchivedAt == null, token)) return false;
        if (actor.PermissionLevel == OrganizationPermissionLevel.Owner) return true;
        return actor.PermissionLevel >= OrganizationPermissionLevel.Manager &&
            await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.OrganizationId == org &&
                x.AgentInstallationId == installation && x.ReportsToOrganizationUserId == actor.Id &&
                x.IsActive && x.ArchivedAt == null, token);
    }
}
