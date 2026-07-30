using CSweet.UI.Components.Employees.Models;
using Microsoft.AspNetCore.Components;

namespace CSweet.UI.Components.Employees;

public partial class EmployeeDetailsPanel
{
    [Parameter]
    public EmployeeViewModel? Employee { get; set; }

    [Parameter]
    public IReadOnlyList<EmployeeViewModel> Employees { get; set; } = [];

    [Parameter]
    public EventCallback<Guid> EmployeeSelected { get; set; }

    [Parameter]
    public EventCallback<EmployeeActionRequest> ActionRequested { get; set; }

    [Parameter]
    public EventCallback<Guid> TeamSelected { get; set; }

    protected IReadOnlyList<EmployeeViewModel> DirectReports => Employee is null
        ? []
        : Employees.Where(x => x.ManagerId == Employee.Id).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    protected Task SelectAsync(Guid id) => EmployeeSelected.InvokeAsync(id);
}
