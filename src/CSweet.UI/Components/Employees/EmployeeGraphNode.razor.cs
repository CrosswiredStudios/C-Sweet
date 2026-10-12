using CSweet.UI.Components.Employees.Models;
using Microsoft.AspNetCore.Components;

namespace CSweet.UI.Components.Employees;

public partial class EmployeeGraphNode
{
    [Parameter, EditorRequired] public EmployeeViewModel Employee { get; set; } = default!;
    [Parameter] public bool Collapsed { get; set; }
    [Parameter] public EventCallback<Guid> BranchToggled { get; set; }
    protected string ReportLabel => Employee.DirectReportCount == 1 ? "1 report" : $"{Employee.DirectReportCount} reports";
    private Task ToggleAsync() => BranchToggled.InvokeAsync(Employee.Id);
}
