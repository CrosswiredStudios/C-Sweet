using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

/// <summary>
/// Resolves a carried-forward role through explicit approved replacements. Never infers
/// authority from a matching title, role elsewhere, pending proposal, or creation time.
/// Reads are scoped to the connected lineage; callers own mutation locks and transactions.
/// </summary>
internal sealed class ApprovedStaffingLineage(CSweetDbContext db, Guid organization)
{
    internal sealed record Resolution(ResourceChangeRequestRecord Current, IReadOnlySet<Guid> SourceIds);
    private readonly Dictionary<Guid, ResourceChangeRequestRecord?> _requests = [];
    private readonly Dictionary<Guid, List<ResourceChangeRequestRecord>> _successors = [];

    private async Task<ResourceChangeRequestRecord?> ReadAsync(Guid id, CancellationToken token)
    {
        if (!_requests.TryGetValue(id, out var value))
            _requests[id] = value = await db.ResourceChangeRequests.AsNoTracking().Include(x => x.Roles)
                .SingleOrDefaultAsync(x => x.OrganizationId == organization && x.Id == id, token);
        return value;
    }

    private async Task<List<ResourceChangeRequestRecord>> SuccessorsAsync(Guid id, CancellationToken token)
    {
        if (!_successors.TryGetValue(id, out var children))
        {
            _successors[id] = children = await db.ResourceChangeRequests.AsNoTracking().Include(x => x.Roles)
                .Where(x => x.OrganizationId == organization && x.SupersedesRequestId == id &&
                    (x.Status == ResourceChangeRequestStatus.Approved || x.Status == ResourceChangeRequestStatus.Superseded)).ToListAsync(token);
            foreach (var child in children) _requests[child.Id] = child;
        }
        return children;
    }

    internal async Task<Resolution?> ResolveAsync(Guid? sourceId, string? role, CancellationToken token)
    {
        if (sourceId is null || string.IsNullOrWhiteSpace(role)) return null;
        var initial = await ReadAsync(sourceId.Value, token);
        if (initial is null || !Carries(initial, role)) return null;
        var seen = new HashSet<Guid>();
        var current = initial;
        while (true)
        {
            if (!seen.Add(current.Id)) return null;
            var children = await SuccessorsAsync(current.Id, token);
            if (current.Status == ResourceChangeRequestStatus.Approved)
            {
                if (children.Count != 0) return null; // Ambiguous authority: do not choose a winner.
                break;
            }
            if (current.Status != ResourceChangeRequestStatus.Superseded || children.Count != 1 ||
                !SameScope(initial, children[0]) || !Carries(children[0], role)) return null;
            current = children[0];
        }
        // Include earlier recommendations for this continuously retained position, even
        // when the user opened a card already linked to the newest approved request.
        var ancestor = initial;
        while (ancestor.SupersedesRequestId is { } parentId)
        {
            if (seen.Contains(parentId)) return null;
            var parent = await ReadAsync(parentId, token);
            if (parent is null || !SameScope(initial, parent) || !Carries(parent, role)) break;
            var siblings = await SuccessorsAsync(parent.Id, token);
            if (parent.Status != ResourceChangeRequestStatus.Superseded || siblings.Count != 1 || siblings[0].Id != ancestor.Id) return null;
            seen.Add(parent.Id);
            ancestor = parent;
        }
        var ids = seen.ToArray();
        var fulfilled = await db.WorkforcePlans.Where(x => x.OrganizationId == organization && x.RoleKey == role &&
            x.SourceResourceChangeRequestId.HasValue && ids.Contains(x.SourceResourceChangeRequestId.Value))
            .SumAsync(x => x.FulfilledHeadcount, token);
        if (fulfilled >= current.Roles.Single(x => x.IsDesired && x.RoleKey == role).Headcount) return null;
        return new(current, seen);
    }

    private static bool Carries(ResourceChangeRequestRecord request, string role) =>
        request.Roles.Any(x => x.IsDesired && x.RoleKey == role && x.Headcount > 0);

    private static bool SameScope(ResourceChangeRequestRecord left, ResourceChangeRequestRecord right) =>
        left.OrganizationId == right.OrganizationId && left.TeamId == right.TeamId &&
        // Approved staffing is team-wide when a team is bound (the same boundary used
        // by ResourceChangeService). A project association does not split that team plan.
        (left.TeamId.HasValue || left.WorkstreamId == right.WorkstreamId);
}
