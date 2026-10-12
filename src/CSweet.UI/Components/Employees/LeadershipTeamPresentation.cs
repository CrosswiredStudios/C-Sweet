using CSweet.Contracts.Core;

namespace CSweet.UI.Components.Employees;

/// <summary>Reporting-based grouping, separate from delivery team assignments and grants.</summary>
public static class LeadershipTeamPresentation
{
    public const string TeamKey = "system:leadership";
    public static readonly Guid TeamId = new("ad694671-179c-46b2-92ea-49326a1f8610");

    public static TeamDirectoryResponse Build(
        TeamDirectoryResponse directory,
        IReadOnlyList<OrganizationUserResponse> employees,
        IReadOnlyList<RoleResponse> roles)
    {
        var ceoRoles = roles.Where(x => string.Equals(x.Name, "CEO", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Id).ToHashSet();
        var ceos = employees.Where(x => x.IsActive && x.ArchivedAt is null &&
            x.RoleId.HasValue && ceoRoles.Contains(x.RoleId.Value)).OrderBy(x => x.Id).ToList();
        if (ceos.Count == 0) return directory;
        var ceoIds = ceos.Select(x => x.Id).ToHashSet();
        var roleNames = roles.ToDictionary(x => x.Id, x => x.Name);
        var members = employees.Where(x => x.IsActive && x.ArchivedAt is null &&
            (ceoIds.Contains(x.Id) || x.ReportsToOrganizationUserId.HasValue &&
                ceoIds.Contains(x.ReportsToOrganizationUserId.Value)))
            .Select(x => new TeamMembershipResponse(x.Id, x.Id, x.DisplayName,
                x.EmployeeType == 1 ? "Agent" : "Human", x.RoleId,
                x.RoleId.HasValue ? roleNames.GetValueOrDefault(x.RoleId.Value) : null,
                ceoIds.Contains(x.Id), x.CreatedAt, null)).ToList();
        var team = new TeamSummaryResponse(TeamId, TeamKey, "Leadership",
            "The CEO and their direct reports. Membership follows reporting relationships.",
            ceos[0].Id, ceos[0].DisplayName, 1, false, members.Count,
            members.Count(x => x.EmployeeType == "Human"), members.Count(x => x.EmployeeType == "Agent"), members);
        return directory with { Teams = directory.Teams.Where(x => x.TeamKey != TeamKey).Append(team).ToList() };
    }
}
