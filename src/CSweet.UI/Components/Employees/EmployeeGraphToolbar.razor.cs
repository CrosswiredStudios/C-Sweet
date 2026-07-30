using CSweet.Contracts.Core;
using Microsoft.AspNetCore.Components;

namespace CSweet.UI.Components.Employees;

public partial class EmployeeGraphToolbar
{
    [Parameter]
    public IReadOnlyList<TeamSummaryResponse> Teams { get; set; } = [];

    [Parameter]
    public Guid? SelectedTeamId { get; set; }

    [Parameter]
    public EventCallback<Guid?> SelectedTeamIdChanged { get; set; }

    [Parameter]
    public int Degrees { get; set; }

    [Parameter]
    public EventCallback<int> DegreesChanged { get; set; }
}
