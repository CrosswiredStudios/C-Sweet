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
    private readonly Dictionary<Guid, string> _accents = new();
    public string? Find(Guid? employeeId) => employeeId is { } id && _images.TryGetValue(id, out var image) ? image : null;

    public string? FindRole(Guid? employeeId) => employeeId is { } id && _roles.TryGetValue(id, out var role) ? role : null;
    public string? FindAccent(Guid? employeeId) => employeeId is { } id && _accents.TryGetValue(id, out var accent) ? accent : null;

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
        var installed = installations.ToDictionary(x => x.Id);
        var images = agents.Where(x => !string.IsNullOrWhiteSpace(x.AgentId) && MarketplaceAgentPresentation.IsAgentImageUrl(x.ImageUrl))
            .GroupBy(x => x.AgentId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().ImageUrl!, StringComparer.OrdinalIgnoreCase);
        var roles = agents.Where(x => !string.IsNullOrWhiteSpace(x.AgentId) && !string.IsNullOrWhiteSpace(x.RoleName))
            .GroupBy(x => x.AgentId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().RoleName!, StringComparer.OrdinalIgnoreCase);
        var accents = agents.Where(x => !string.IsNullOrWhiteSpace(x.AgentId) && AgentCatalogBranding.IsAccentColor(x.AccentColor))
            .GroupBy(x => x.AgentId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().AccentColor!, StringComparer.OrdinalIgnoreCase);
        foreach (var employee in employees.Where(x => x.EmployeeType == 1))
        {
            if (employee.AgentInstallationId is not { } installationId || !installed.TryGetValue(installationId, out var installation)) continue;
            if (MarketplaceAgentPresentation.IsAgentImageUrl(installation.ImageUrl))
                result._images[employee.Id] = installation.ImageUrl!;
            else if (images.TryGetValue(installation.AgentId, out var image))
                result._images[employee.Id] = image;
            if (!string.IsNullOrWhiteSpace(installation.RoleName))
                result._roles[employee.Id] = installation.RoleName;
            else if (roles.TryGetValue(installation.AgentId, out var role))
                result._roles[employee.Id] = role;
            if (AgentCatalogBranding.IsAccentColor(installation.AccentColor))
                result._accents[employee.Id] = installation.AccentColor!;
            else if (accents.TryGetValue(installation.AgentId, out var accent))
                result._accents[employee.Id] = accent;
        }
        return result;
    }
}
