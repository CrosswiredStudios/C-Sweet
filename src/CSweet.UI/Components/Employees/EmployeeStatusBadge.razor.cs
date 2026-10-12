using CSweet.UI.Components.Employees.Models;
using Microsoft.AspNetCore.Components;

namespace CSweet.UI.Components.Employees;

public partial class EmployeeStatusBadge
{
    [Parameter]
    public EmployeeRuntimeStatus Status { get; set; }

    [Parameter] public bool Compact { get; set; }
    protected string Label => Status switch
    {
        EmployeeRuntimeStatus.NotTracked => "Not tracked",
        EmployeeRuntimeStatus.Checking => "Checking",
        EmployeeRuntimeStatus.Online => "Online",
        EmployeeRuntimeStatus.Transitional => "Transitioning",
        EmployeeRuntimeStatus.Offline => "Offline",
        EmployeeRuntimeStatus.Failed => "Failed",
        _ => "Unknown"
    };

    protected string CssClass => Status.ToString().ToLowerInvariant();
}
