using CSweet.UI.Components.Employees.Models;
using Microsoft.AspNetCore.Components;

namespace CSweet.UI.Components.Employees;

public partial class EmployeeDirectoryCard
{
    [Parameter, EditorRequired]
    public EmployeeViewModel Employee { get; set; } = default!;

    [Parameter]
    public Guid? SelectedId { get; set; }

    [Parameter]
    public EventCallback<Guid> EmployeeSelected { get; set; }

    [Parameter]
    public EventCallback<EmployeeActionRequest> ActionRequested { get; set; }

}
