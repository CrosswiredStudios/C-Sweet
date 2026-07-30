using CSweet.UI.Components.Employees.Models;
using Microsoft.AspNetCore.Components;

namespace CSweet.UI.Components.Employees;

public partial class EmployeeViewToggle
{
    [Parameter]
    public EmployeeViewKind Value { get; set; }

    [Parameter]
    public EventCallback<EmployeeViewKind> ValueChanged { get; set; }

    protected Task SelectGraphAsync() => ValueChanged.InvokeAsync(EmployeeViewKind.Graph);
    protected Task SelectTeamsAsync() => ValueChanged.InvokeAsync(EmployeeViewKind.Teams);
    protected Task SelectDirectoryAsync() => ValueChanged.InvokeAsync(EmployeeViewKind.Directory);
}
