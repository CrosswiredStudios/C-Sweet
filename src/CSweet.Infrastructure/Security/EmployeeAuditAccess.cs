using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Security;

public static class EmployeeAuditAccess
{
    public static async Task<bool> CanReadAsync(CSweetDbContext db, Guid organizationId, Guid employeeId, Guid applicationUserId, CancellationToken token)
    {
        var people = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.IsActive).ToListAsync(token);
        var actor = people.SingleOrDefault(x => x.ApplicationUserId == applicationUserId && x.EmployeeType == EmployeeType.Human);
        return actor is not null && CanRead(people, employeeId, actor.Id);
    }

    public static bool CanRead(IReadOnlyList<OrganizationUser> people, Guid employeeId, Guid actorId)
    {
        var actor = people.FirstOrDefault(x => x.Id == actorId && x.IsActive && x.EmployeeType == EmployeeType.Human);
        var target = people.FirstOrDefault(x => x.Id == employeeId && x.IsActive && x.EmployeeType == EmployeeType.Agent);
        if (actor is null || target is null || actor.OrganizationId != target.OrganizationId) return false;
        if (actor.PermissionLevel == OrganizationPermissionLevel.Owner) return true;
        var seen = new HashSet<Guid>();
        var managers = new HashSet<Guid>();
        while (target is not null)
        {
            if (!seen.Add(target.Id)) return false;
            if (target.ReportsToOrganizationUserId is not Guid manager) break;
            managers.Add(manager);
            target = people.FirstOrDefault(x => x.Id == manager && x.IsActive && x.OrganizationId == actor.OrganizationId);
        }
        return managers.Contains(actorId);
    }
}
