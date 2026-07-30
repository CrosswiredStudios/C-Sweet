using CSweet.UI.Components.Employees.Models;
using Microsoft.AspNetCore.Components;

namespace CSweet.UI.Components.Employees;

public partial class EmployeeGraphView
{
    private Guid? _teamFilter;
    private EmployeeGraphModel _graph = new([], [], 720, 360, 260, 144);

    [Parameter]
    public IReadOnlyList<EmployeeViewModel> Employees { get; set; } = [];

    [Parameter]
    public Guid? SelectedId { get; set; }

    [Parameter]
    public EventCallback<Guid> SelectedIdChanged { get; set; }

    [Parameter]
    public int Degrees { get; set; } = 2;

    [Parameter]
    public EventCallback<int> DegreesChanged { get; set; }

    [Parameter]
    public EventCallback<EmployeeActionRequest> ActionRequested { get; set; }

    [Parameter]
    public IReadOnlyList<CSweet.Contracts.Core.TeamSummaryResponse> Teams { get; set; } = [];

    [Parameter]
    public EventCallback<Guid> TeamSelected { get; set; }

    protected EmployeeGraphModel Graph => _graph;
    protected EmployeeViewModel? SelectedEmployee => Employees.FirstOrDefault(x => x.Id == SelectedId);

    protected override void OnParametersSet() =>
        _graph = EmployeeHierarchyService.Build(Employees, SelectedId, Degrees);

    protected Task SelectAsync(Guid id) => SelectedIdChanged.InvokeAsync(id);
    protected Task ChangeDegreesAsync(int value) => DegreesChanged.InvokeAsync(value);
    protected void ChangeTeamFilter(Guid? teamId) => _teamFilter = teamId;
}
