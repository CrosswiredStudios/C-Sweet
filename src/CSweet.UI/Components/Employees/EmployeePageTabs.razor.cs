using Microsoft.AspNetCore.Components;

namespace CSweet.UI.Components.Employees;

public partial class EmployeePageTabs
{
    [Parameter]
    public Guid OrganizationId { get; set; }

    [Parameter]
    public bool HiringSelected { get; set; }

    protected string TeamUrl => $"/organizations/{OrganizationId}/employees?tab=team";
    protected string HiringUrl => $"/organizations/{OrganizationId}/employees?tab=hiring";
}
