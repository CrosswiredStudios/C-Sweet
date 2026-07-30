using CSweet.UI.Components.Employees.Models;

namespace CSweet.UI.Components.Employees;

public static class EmployeeDirectoryFilterService
{
    public static IReadOnlyList<EmployeeViewModel> Apply(
        IEnumerable<EmployeeViewModel> employees,
        EmployeeDirectoryFilter filter)
    {
        var query = employees;
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            query = query.Where(x =>
                x.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                x.RoleLabel.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (x.RoleName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                x.Teams.Any(team =>
                    team.TeamName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    (team.TeamRole?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)));
        }

        query = filter.Role switch
        {
            "unassigned" => query.Where(x => !x.RoleId.HasValue),
            "all" => query,
            _ when Guid.TryParse(filter.Role, out var roleId) => query.Where(x => x.RoleId == roleId),
            _ => query
        };

        query = filter.Team switch
        {
            "unassigned" => query.Where(x => x.Teams.Count == 0),
            "all" => query,
            _ when Guid.TryParse(filter.Team, out var teamId) =>
                query.Where(x => x.Teams.Any(team => team.TeamId == teamId)),
            _ => query
        };

        query = filter.Type switch
        {
            EmployeeTypeFilter.Human => query.Where(x => !x.IsAgent),
            EmployeeTypeFilter.Agent => query.Where(x => x.IsAgent),
            _ => query
        };

        if (filter.Status.HasValue)
        {
            query = query.Where(x => x.Status == filter.Status.Value);
        }

        return query.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
