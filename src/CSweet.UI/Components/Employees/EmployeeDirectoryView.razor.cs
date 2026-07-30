using CSweet.Contracts.Core;
using CSweet.UI.Components.Employees.Models;
using Microsoft.AspNetCore.Components;

namespace CSweet.UI.Components.Employees;

public partial class EmployeeDirectoryView
{
    [Parameter]
    public IReadOnlyList<EmployeeViewModel> Employees { get; set; } = [];

    [Parameter]
    public IReadOnlyList<RoleResponse> Roles { get; set; } = [];

    [Parameter]
    public IReadOnlyList<TeamSummaryResponse> Teams { get; set; } = [];

    [Parameter]
    public EmployeeDirectoryFilter Filter { get; set; } = new();

    [Parameter]
    public EventCallback<EmployeeDirectoryFilter> FilterChanged { get; set; }

    [Parameter]
    public Guid? SelectedId { get; set; }

    [Parameter]
    public EventCallback<Guid> EmployeeSelected { get; set; }

    [Parameter]
    public EventCallback<EmployeeActionRequest> ActionRequested { get; set; }

    protected IReadOnlyList<EmployeeViewModel> FilteredEmployees => EmployeeDirectoryFilterService.Apply(Employees, Filter);
    protected string ResultLabel => FilteredEmployees.Count == 1 ? "1 employee" : $"{FilteredEmployees.Count} employees";

    protected Task ChangeSearchAsync(string value) => FilterChanged.InvokeAsync(Filter with { Search = value ?? string.Empty });
    protected Task ChangeRoleAsync(string value) => FilterChanged.InvokeAsync(Filter with { Role = value ?? "all" });
    protected Task ChangeTeamAsync(string value) => FilterChanged.InvokeAsync(Filter with { Team = value ?? "all" });
    protected Task ChangeTypeAsync(EmployeeTypeFilter value) => FilterChanged.InvokeAsync(Filter with { Type = value });
    protected Task ChangeStatusAsync(EmployeeRuntimeStatus? value) => FilterChanged.InvokeAsync(Filter with { Status = value });
    protected Task ClearAsync() => FilterChanged.InvokeAsync(new EmployeeDirectoryFilter());
}
