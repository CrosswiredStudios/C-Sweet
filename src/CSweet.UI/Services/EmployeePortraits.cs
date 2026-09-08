using CSweet.Agent.SDK;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Core;

namespace CSweet.UI.Services;

public sealed class EmployeePortraits
{
    private readonly Dictionary<Guid, string> _images = new();
    public string? Find(Guid? employeeId) => employeeId is { } id && _images.TryGetValue(id, out var image) ? image : null;

    public static EmployeePortraits Build(IReadOnlyList<OrganizationUserResponse> employees,
        IReadOnlyList<AgentInstallationResponse> installations, IReadOnlyList<AvailableAgent> agents)
    {
        var result = new EmployeePortraits();
        var identities = installations.ToDictionary(x => x.Id, x => x.AgentId);
        var images = agents.Where(x => !string.IsNullOrWhiteSpace(x.AgentId) && MarketplaceAgentPresentation.IsAgentImageUrl(x.ImageUrl))
            .GroupBy(x => x.AgentId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().ImageUrl!, StringComparer.OrdinalIgnoreCase);
        foreach (var employee in employees.Where(x => x.EmployeeType == 1))
            if (employee.AgentInstallationId is { } installation && identities.TryGetValue(installation, out var agentId)
                && images.TryGetValue(agentId, out var image)) result._images[employee.Id] = image;
        return result;
    }
}
