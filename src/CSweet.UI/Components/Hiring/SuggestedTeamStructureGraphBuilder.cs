using CSweet.Contracts.Core;

namespace CSweet.UI.Components.Hiring;

public static class SuggestedTeamStructureGraphBuilder
{
    private const int MaximumVisibleSeats = 5;

    public static SuggestedTeamStructureGraphModel Build(
        ResourceChangeRequestResponse request,
        string? productManagerDisplayName,
        string? managerDisplayName,
        Func<string, string, string>? roleTitle = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var changedKeys = request.Deltas.Where(x => x.ChangeKind is not "Unchanged" and not "Remove")
            .Select(x => x.Role.RoleKey).ToHashSet(StringComparer.Ordinal);
        // Preserve existing ancestors needed to explain a changed role's reporting path.
        var displayedKeys = new HashSet<string>(changedKeys, StringComparer.Ordinal);
        var byKey = request.Roles.ToDictionary(x => x.RoleKey, StringComparer.Ordinal);
        foreach (var key in changedKeys)
        {
            var current = byKey.GetValueOrDefault(key);
            while (current?.ReportsToRoleKey is { } parent && byKey.TryGetValue(parent, out current) && displayedKeys.Add(parent)) { }
        }
        var retained = request.Roles.Where(x => !changedKeys.Contains(x.RoleKey))
            .Select(x => $"{roleTitle?.Invoke(x.RoleKey, x.Title) ?? x.Title} ({x.Headcount} existing)").ToArray();
        var roles = request.Roles.Where(x => displayedKeys.Contains(x.RoleKey))
            .Select(x => x with { Title = roleTitle?.Invoke(x.RoleKey, x.Title) ?? x.Title })
            .OrderBy(role => role.Priority)
            .ThenBy(role => role.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(role => role.RoleKey, StringComparer.Ordinal)
            .ToArray();
        var roleKeys = roles.Select(role => role.RoleKey).ToHashSet(StringComparer.Ordinal);
        var childrenByParent = roles
            .Where(role => role.ReportsToRoleKey is not null && roleKeys.Contains(role.ReportsToRoleKey))
            .GroupBy(role => role.ReportsToRoleKey!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ResourceChangeRole>)group.ToArray(),
                StringComparer.Ordinal);
        var rootRoles = roles
            .Where(role => role.ReportsToRoleKey is null || !roleKeys.Contains(role.ReportsToRoleKey))
            .Select(role => BuildCohort(role, childrenByParent, new HashSet<string>(StringComparer.Ordinal)))
            .ToArray();

        return new SuggestedTeamStructureGraphModel(
            CleanName(managerDisplayName, "Manager"),
            CleanName(productManagerDisplayName, "Product manager"),
            rootRoles) { RetainedRoles = retained };
    }

    private static SuggestedTeamRoleCohort BuildCohort(
        ResourceChangeRole role,
        IReadOnlyDictionary<string, IReadOnlyList<ResourceChangeRole>> childrenByParent,
        HashSet<string> ancestors)
    {
        if (!ancestors.Add(role.RoleKey))
            return CreateCohort(role, []);

        var children = childrenByParent.TryGetValue(role.RoleKey, out var directReports)
            ? directReports
                .OrderBy(child => child.Priority)
                .ThenBy(child => child.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(child => child.RoleKey, StringComparer.Ordinal)
                .Select(child => BuildCohort(child, childrenByParent, new HashSet<string>(ancestors, StringComparer.Ordinal)))
                .ToArray()
            : [];

        return CreateCohort(role, children);
    }

    private static SuggestedTeamRoleCohort CreateCohort(
        ResourceChangeRole role,
        IReadOnlyList<SuggestedTeamRoleCohort> children)
    {
        var visibleSeatCount = Math.Min(role.Headcount, MaximumVisibleSeats);
        var seats = Enumerable.Range(1, visibleSeatCount)
            .Select(index => new SuggestedTeamSeatNode(
                $"{role.RoleKey}:{index}",
                role.Title,
                role.Headcount == 1 ? "1 seat" : $"Seat {index} of {role.Headcount}",
                false))
            .ToList();
        if (role.Headcount > MaximumVisibleSeats)
        {
            seats.Add(new SuggestedTeamSeatNode(
                $"{role.RoleKey}:remainder",
                $"+{role.Headcount - MaximumVisibleSeats} more",
                role.Title,
                true));
        }

        return new SuggestedTeamRoleCohort(
            role.RoleKey,
            role.Title,
            role.Headcount,
            role.Priority,
            role.Timing,
            role.HumanRequired,
            seats,
            children);
    }

    private static string CleanName(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

public sealed record SuggestedTeamStructureGraphModel(
    string ManagerDisplayName,
    string ProductManagerDisplayName,
    IReadOnlyList<SuggestedTeamRoleCohort> RoleCohorts)
{
    public IReadOnlyList<string> RetainedRoles { get; init; } = [];
}

public sealed record SuggestedTeamRoleCohort(
    string RoleKey,
    string Title,
    int Headcount,
    int Priority,
    string Timing,
    bool HumanRequired,
    IReadOnlyList<SuggestedTeamSeatNode> Seats,
    IReadOnlyList<SuggestedTeamRoleCohort> Children);

public sealed record SuggestedTeamSeatNode(
    string Key,
    string Label,
    string Detail,
    bool IsRemainder);
