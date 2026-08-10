using CSweet.Application.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed class EmployeeHierarchyAccessService(CSweetDbContext db) : IEmployeeHierarchyAccessService
{
    public Task<Guid?> ResolveOrganizationUserIdAsync(Guid organizationId, Guid applicationUserId,
        CancellationToken cancellationToken = default) =>
        db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.ApplicationUserId == applicationUserId && x.IsActive)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlySet<Guid>> GetSelfAndDescendantsAsync(Guid organizationId,
        Guid organizationUserId, CancellationToken cancellationToken = default)
    {
        var employees = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.IsActive)
            .Select(x => new { x.Id, x.ReportsToOrganizationUserId })
            .ToListAsync(cancellationToken);
        if (!employees.Any(x => x.Id == organizationUserId))
            return new HashSet<Guid>();
        var children = employees.Where(x => x.ReportsToOrganizationUserId.HasValue)
            .GroupBy(x => x.ReportsToOrganizationUserId!.Value)
            .ToDictionary(x => x.Key, x => x.Select(y => y.Id).ToArray());
        var result = new HashSet<Guid>();
        var pending = new Queue<Guid>();
        pending.Enqueue(organizationUserId);
        while (pending.TryDequeue(out var current))
        {
            // Repeated nodes prove a hierarchy cycle. Fail closed rather than returning partial access.
            if (!result.Add(current))
                return new HashSet<Guid>();
            if (children.TryGetValue(current, out var directReports))
                foreach (var child in directReports)
                    pending.Enqueue(child);
        }
        return result;
    }

    public async Task<bool> CanAccessSensitiveAsync(Guid organizationId,
        Guid actorOrganizationUserId, Guid employeeId, CancellationToken cancellationToken = default) =>
        (await GetSelfAndDescendantsAsync(organizationId, actorOrganizationUserId, cancellationToken))
            .Contains(employeeId);
}
