using CSweet.Contracts.Agents;
using CSweet.Contracts.Core;
using CSweet.UI.Components.Employees;
using CSweet.UI.Components.Employees.Models;

namespace CSweet.UnitTests;

public sealed class EmployeePresentationTests
{
    [Fact]
    public void Hierarchy_FocusesSelf_AndLimitsByDegrees()
    {
        var root = Employee("Self");
        var manager = Employee("Manager", root.Id);
        var specialist = Employee("Specialist", manager.Id);
        var intern = Employee("Intern", specialist.Id);
        var viewModels = Present([root, manager, specialist, intern]);

        Assert.Equal(root.Id, EmployeeHierarchyService.InitialFocus(viewModels));
        Assert.Equal(2, EmployeeHierarchyService.Build(viewModels, root.Id, 1).Nodes.Count);
        Assert.Equal(3, EmployeeHierarchyService.Build(viewModels, root.Id, 2).Nodes.Count);
        Assert.Equal(4, EmployeeHierarchyService.Build(viewModels, root.Id, 3).Nodes.Count);
    }

    [Fact]
    public void Hierarchy_HandlesCyclesMissingManagersAndMultipleRoots()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var first = Employee("First", secondId, firstId);
        var second = Employee("Second", firstId, secondId);
        var root = Employee("Root");
        var orphan = Employee("Orphan", Guid.NewGuid());
        var viewModels = Present([first, second, root, orphan]);

        var graph = EmployeeHierarchyService.Build(viewModels, first.Id, 3);

        Assert.Equal(2, graph.Nodes.Count);
        Assert.Equal(graph.Nodes.Count, graph.Nodes.Select(x => x.Employee.Id).Distinct().Count());
        Assert.NotNull(EmployeeHierarchyService.Build(viewModels, root.Id, 2));
        Assert.NotNull(EmployeeHierarchyService.Build(viewModels, orphan.Id, 2));
    }

    [Fact]
    public void DirectoryFilter_CombinesSearchRoleTypeAndStatus()
    {
        var roleId = Guid.NewGuid();
        var human = ViewModel(Employee("Alex"), "Operations", roleId, false, EmployeeRuntimeStatus.NotTracked);
        var agent = ViewModel(Employee("Scout", employeeType: 1), "Security Agent", null, true, EmployeeRuntimeStatus.Online);
        var offlineAgent = ViewModel(Employee("Writer", employeeType: 1), "Content Agent", null, true, EmployeeRuntimeStatus.Offline);

        var result = EmployeeDirectoryFilterService.Apply(
            [human, agent, offlineAgent],
            new EmployeeDirectoryFilter("security", "all", EmployeeTypeFilter.Agent, EmployeeRuntimeStatus.Online));

        Assert.Single(result);
        Assert.Equal(agent.Id, result[0].Id);
        Assert.Single(EmployeeDirectoryFilterService.Apply([human, agent], new EmployeeDirectoryFilter(Role: roleId.ToString())));
        Assert.Equal(3, EmployeeDirectoryFilterService.Apply([human, agent, offlineAgent], new EmployeeDirectoryFilter()).Count);
    }

    [Fact]
    public void Presentation_MapsProtectedAndAgentActions()
    {
        var self = Employee("Self");
        var agent = Employee("Scout", self.Id, employeeType: 1) with
        {
            AgentInstallationId = Guid.NewGuid(),
            SupportsAgentConfiguration = true
        };
        var installation = Installation(agent.AgentInstallationId!.Value);
        var runtime = new AgentRuntimeReadinessResponse(
            installation.Id, Guid.NewGuid(), AgentRuntimeReadinessStages.Ready, "Running", null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, false);

        var result = EmployeePresentationService.Build(
            [self, agent], [], [], [installation], new Dictionary<Guid, AgentRuntimeReadinessResponse> { [installation.Id] = runtime });

        var selfView = result.Single(x => x.IsSelf);
        var agentView = result.Single(x => x.IsAgent);
        Assert.Contains(EmployeeAction.ChangeRole, selfView.Actions);
        Assert.DoesNotContain(EmployeeAction.Fire, selfView.Actions);
        Assert.Contains(EmployeeAction.OpenChat, agentView.Actions);
        Assert.Contains(EmployeeAction.StopRuntime, agentView.Actions);
        Assert.Contains(EmployeeAction.Configure, agentView.Actions);
        Assert.Contains(EmployeeAction.Fire, agentView.Actions);
        Assert.DoesNotContain(EmployeeAction.StartRuntime, agentView.Actions);
    }

    [Fact]
    public void Presentation_RepeatsHumanAcrossTeams_ButKeepsAgentSingleAndSearchableByTeamRole()
    {
        var human = Employee("Taylor");
        var agent = Employee("Dev instance", employeeType: 1);
        var alpha = Team("Alpha", human, agent, "Quality Lead");
        var beta = Team("Beta", human, null, "Contract Architect");
        var directory = new TeamDirectoryResponse(Guid.NewGuid(), true, [alpha, beta]);

        var presented = EmployeePresentationService.Build(
            [human, agent],
            [],
            [],
            [],
            new Dictionary<Guid, AgentRuntimeReadinessResponse>(),
            teamDirectory: directory);

        Assert.Equal(2, presented.Single(x => x.Id == human.Id).Teams.Count);
        Assert.Single(presented.Single(x => x.Id == agent.Id).Teams);
        var betaHumans = EmployeeDirectoryFilterService.Apply(
            presented,
            new EmployeeDirectoryFilter(Team: beta.Id.ToString()));
        Assert.Single(betaHumans);
        Assert.Equal(human.Id, betaHumans[0].Id);
        Assert.Single(EmployeeDirectoryFilterService.Apply(
            presented,
            new EmployeeDirectoryFilter(Search: "Contract Architect")));
    }

    private static IReadOnlyList<EmployeeViewModel> Present(IReadOnlyList<OrganizationUserResponse> employees) =>
        EmployeePresentationService.Build(employees, [], [], [], new Dictionary<Guid, AgentRuntimeReadinessResponse>());

    private static OrganizationUserResponse Employee(
        string name,
        Guid? managerId = null,
        Guid? id = null,
        int employeeType = 0) =>
        new(id ?? Guid.NewGuid(), Guid.NewGuid(), managerId, null, null, name, null, employeeType, 0, DateTimeOffset.UtcNow);

    private static EmployeeViewModel ViewModel(
        OrganizationUserResponse employee,
        string label,
        Guid? roleId,
        bool isAgent,
        EmployeeRuntimeStatus status) =>
        new(employee with { RoleId = roleId }, employee.DisplayName, employee.DisplayName[..1], label, label, null,
            isAgent, false, status, 0, [], false, false, false, []);

    private static AgentInstallationResponse Installation(Guid id) =>
        new(id, Guid.NewGuid(), "business", "agent", "Agent", "1.0.0", "Publisher", "commit", true,
            [], [], [], [], [], 512, 50,
            new AgentScheduleResponse(Guid.NewGuid(), "AlwaysOn", 60, null, null, null, null, 300, 3, 0, null, "Skip", true),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static TeamSummaryResponse Team(
        string name,
        OrganizationUserResponse human,
        OrganizationUserResponse? agent,
        string humanRole)
    {
        var teamId = Guid.NewGuid();
        var members = new List<TeamMembershipResponse>
        {
            new(Guid.NewGuid(), human.Id, human.DisplayName, "Human", null, humanRole, true,
                DateTimeOffset.UtcNow, null)
        };
        if (agent is not null)
        {
            members.Add(new TeamMembershipResponse(
                Guid.NewGuid(), agent.Id, agent.DisplayName, "Agent", null, "Developer", false,
                DateTimeOffset.UtcNow, null));
        }
        return new TeamSummaryResponse(
            teamId,
            name.ToLowerInvariant(),
            name,
            $"{name} team",
            human.Id,
            human.DisplayName,
            1,
            false,
            members.Count,
            1,
            agent is null ? 0 : 1,
            members);
    }
}
