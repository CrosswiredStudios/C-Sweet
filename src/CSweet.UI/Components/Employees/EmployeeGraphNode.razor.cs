using CSweet.UI.Components.Employees.Models;
using Microsoft.AspNetCore.Components;

namespace CSweet.UI.Components.Employees;

public partial class EmployeeGraphNode
{
    [Parameter, EditorRequired]
    public EmployeeViewModel Employee { get; set; } = default!;

    [Parameter]
    public bool Selected { get; set; }

    [Parameter]
    public EventCallback<Guid> SelectedChanged { get; set; }

    protected string ReportLabel => Employee.DirectReportCount == 1
        ? "1 direct report"
        : $"{Employee.DirectReportCount} direct reports";

    protected string? SingleTeamBorder => Employee.Teams.Count == 1
        ? $"border-color:{Employee.Teams[0].Color}"
        : null;

    protected Task SelectAsync() => SelectedChanged.InvokeAsync(Employee.Id);
}
