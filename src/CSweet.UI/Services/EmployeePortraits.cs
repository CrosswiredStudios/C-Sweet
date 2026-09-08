using CSweet.Agent.SDK;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Core;

namespace CSweet.UI.Services;

public sealed class EmployeePortraits
{
    private readonly Dictionary<string, string> _roleNames = new(StringComparer.Ordinal);
    public string RoleTitle(string roleKey, string title) =>
        string.Equals(roleKey, title, StringComparison.Ordinal) && _roleNames.TryGetValue(roleKey, out var name) ? name : title;

    private readonly Dictionary<Guid, string> _images = new();
    private readonly Dictionary<Guid, string> _roles = new();
    public string? Find(Guid? employeeId) => employeeId is { } id && _images.TryGetValue(id, out var image) ? image : null;

    public string? FindRole(Guid? employeeId) => employeeId is { } id && _roles.TryGetValue(id, out var role) ? role : null;

    public static EmployeePortraits Build(IReadOnlyList<OrganizationUserResponse> employees,
        IReadOnlyList<AgentInstallationResponse> installations, IReadOnlyList<AvailableAgent> agents)
    {
        var result = new EmployeePortraits();
        foreach (var group in agents.Where(x => !string.IsNullOrWhiteSpace(x.RoleKey) && !string.IsNullOrWhiteSpace(x.RoleName))
                     .GroupBy(x => x.RoleKey!, StringComparer.Ordinal))
        {
            var names = group.Select(x => x.RoleName!).Distinct(StringComparer.Ordinal).ToArray();
            if (names.Length == 1) result._roleNames[group.Key] = names[0];
        }
        var identities = installations.ToDictionary(x => x.Id, x => x.AgentId);
        var images = agents.Where(x => !string.IsNullOrWhiteSpace(x.AgentId) && MarketplaceAgentPresentation.IsAgentImageUrl(x.ImageUrl))
            .GroupBy(x => x.AgentId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().ImageUrl!, StringComparer.OrdinalIgnoreCase);
        var roles = agents.Where(x => !string.IsNullOrWhiteSpace(x.AgentId) && !string.IsNullOrWhiteSpace(x.RoleName))
            .GroupBy(x => x.AgentId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().RoleName!, StringComparer.OrdinalIgnoreCase);
        foreach (var employee in employees.Where(x => x.EmployeeType == 1))
        {
            if (employee.AgentInstallationId is not { } installation || !identities.TryGetValue(installation, out var agentId)) continue;
            if (images.TryGetValue(agentId, out var image)) result._images[employee.Id] = image;
            if (roles.TryGetValue(agentId, out var role)) result._roles[employee.Id] = role;
        }
        return result;
    }
}
