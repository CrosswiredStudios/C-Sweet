using CSweet.Contracts.Agents;
using CSweet.Contracts.Core;
using CSweet.UI.Components.Employees.Models;

namespace CSweet.UI.Components.Employees;

public static class EmployeePresentationService
{
    public static IReadOnlyList<EmployeeViewModel> Build(
        IReadOnlyList<OrganizationUserResponse> employees,
        IReadOnlyList<RoleResponse> roles,
        IReadOnlyList<WorkerResponse> workers,
        IReadOnlyList<AgentInstallationResponse> installations,
        IReadOnlyDictionary<Guid, AgentRuntimeReadinessResponse> runtimeStatuses,
        Guid? managingRuntimeInstallationId = null,
        TeamDirectoryResponse? teamDirectory = null)
    {
        var rolesById = roles.ToDictionary(x => x.Id);
        var workersById = workers.ToDictionary(x => x.Id);
        var employeesById = employees.ToDictionary(x => x.Id);
        var installationsById = installations.ToDictionary(x => x.Id);

        return employees
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(employee =>
            {
                rolesById.TryGetValue(employee.RoleId ?? Guid.Empty, out var role);
                workersById.TryGetValue(employee.WorkerId ?? Guid.Empty, out var worker);
                employeesById.TryGetValue(employee.ReportsToOrganizationUserId ?? Guid.Empty, out var manager);
                installationsById.TryGetValue(employee.AgentInstallationId ?? Guid.Empty, out var installation);
                runtimeStatuses.TryGetValue(employee.AgentInstallationId ?? Guid.Empty, out var runtime);

                var isAgent = employee.EmployeeType == 1;
                var isSelf = employee.ApplicationUserId.HasValue ||
                    string.Equals(employee.DisplayName.Trim(), "Self", StringComparison.OrdinalIgnoreCase);
                var status = RuntimeStatus(isAgent, employee.AgentInstallationId, runtime);
                var directReports = employees
                    .Where(x => x.ReportsToOrganizationUserId == employee.Id)
                    .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.Id)
                    .ToArray();
                var runtimeBusy = employee.AgentInstallationId == managingRuntimeInstallationId;
                var canStart = isAgent && employee.AgentInstallationId.HasValue && !runtimeBusy &&
                    status is EmployeeRuntimeStatus.Checking or EmployeeRuntimeStatus.Offline or EmployeeRuntimeStatus.Failed;
                var canStop = isAgent && installation?.IsEnabled == true && !runtimeBusy &&
                    status is EmployeeRuntimeStatus.Online or EmployeeRuntimeStatus.Transitional;
                var roleLabel = role?.Name ?? (isAgent ? worker?.Name ?? "Agent" : "Employee");
                var actions = BuildActions(employee, isAgent, isSelf, canStart, canStop);
                var memberships = (teamDirectory?.Teams ?? [])
                    .SelectMany(team => team.Members
                        .Where(membership => membership.OrganizationUserId == employee.Id &&
                            membership.EndedAt is null)
                        .Select(membership => new EmployeeTeamViewModel(
                            team.Id,
                            team.Name,
                            membership.TeamRoleName,
                            membership.IsLead,
                            team.IsArchived,
                            TeamColor(team.TeamKey))))
                    .OrderBy(x => x.TeamName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new EmployeeViewModel(
                    employee,
                    employee.DisplayName,
                    Initials(employee.DisplayName),
                    roleLabel,
                    role?.Name,
                    manager?.DisplayName,
                    isAgent,
                    isSelf,
                    status,
                    directReports.Length,
                    directReports,
                    canStart,
                    canStop,
                    runtimeBusy,
                    actions)
                {
                    Teams = memberships
                };
            })
            .ToArray();
    }

    private static EmployeeRuntimeStatus RuntimeStatus(
        bool isAgent,
        Guid? installationId,
        AgentRuntimeReadinessResponse? runtime)
    {
        if (!isAgent)
        {
            return EmployeeRuntimeStatus.NotTracked;
        }

        if (!installationId.HasValue)
        {
            return EmployeeRuntimeStatus.Offline;
        }

        return runtime?.Stage switch
        {
            AgentRuntimeReadinessStages.Ready => EmployeeRuntimeStatus.Online,
            AgentRuntimeReadinessStages.Queued or
            AgentRuntimeReadinessStages.StartingContainer or
            AgentRuntimeReadinessStages.WaitingForMcpSession or
            AgentRuntimeReadinessStages.Stopping => EmployeeRuntimeStatus.Transitional,
            AgentRuntimeReadinessStages.Failed => EmployeeRuntimeStatus.Failed,
            AgentRuntimeReadinessStages.Offline => EmployeeRuntimeStatus.Offline,
            _ => EmployeeRuntimeStatus.Checking
        };
    }

    private static IReadOnlyList<EmployeeAction> BuildActions(
        OrganizationUserResponse employee,
        bool isAgent,
        bool isSelf,
        bool canStart,
        bool canStop)
    {
        var actions = new List<EmployeeAction>();
        if (isAgent && !isSelf)
        {
            actions.Add(EmployeeAction.OpenChat);
            if (canStart) actions.Add(EmployeeAction.StartRuntime);
            if (canStop) actions.Add(EmployeeAction.StopRuntime);
            if (employee.AgentInstallationId.HasValue)
            {
                actions.Add(EmployeeAction.OpenConsole);
                actions.Add(EmployeeAction.OpenMemory);
            }
            if (employee.SupportsAgentConfiguration) actions.Add(EmployeeAction.Configure);
        }

        actions.Add(isSelf ? EmployeeAction.ChangeRole : EmployeeAction.Fire);
        return actions;
    }

    private static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            0 => "?",
            1 => words[0][..1].ToUpperInvariant(),
            _ => $"{words[0][..1]}{words[^1][..1]}".ToUpperInvariant()
        };
    }

    public static string TeamColor(string key)
    {
        var palette = new[]
        {
            "#2563eb", "#7c3aed", "#c026d3", "#dc2626",
            "#d97706", "#059669", "#0891b2", "#4f46e5"
        };
        var hash = key.Aggregate(17, (current, character) => unchecked(current * 31 + character));
        return palette[(hash & int.MaxValue) % palette.Length];
    }
}
