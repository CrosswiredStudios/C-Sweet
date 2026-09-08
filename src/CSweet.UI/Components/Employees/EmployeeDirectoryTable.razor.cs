using CSweet.UI.Components.Employees.Models;
using Microsoft.AspNetCore.Components;

namespace CSweet.UI.Components.Employees;

public partial class EmployeeDirectoryTable
{
    [Parameter]
    public IReadOnlyList<EmployeeViewModel> Employees { get; set; } = [];

    [Parameter]
    public Guid? SelectedId { get; set; }

    [Parameter]
    public EventCallback<Guid> EmployeeSelected { get; set; }

    [Parameter]
    public EventCallback<EmployeeActionRequest> ActionRequested { get; set; }

}
